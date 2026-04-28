using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;

namespace ABook.Api.Controllers;

[ApiController]
[Route("api")]
public class ModelsController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;

    public ModelsController(IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    private string OllamaEndpoint =>
        _config["Ollama:Endpoint"] ?? "http://host.docker.internal:11434";

    [HttpGet("models")]
    public async Task<IActionResult> GetModels(
        [FromQuery] string? endpoint,
        [FromQuery] string? provider,
        [FromQuery] string? apiKey)
    {
        var baseUrl = (endpoint ?? OllamaEndpoint).TrimEnd('/');
        try
        {
            var client = _httpClientFactory.CreateClient("ollama");

            if (string.Equals(provider, "GoogleAIStudio", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(apiKey))
                    return BadRequest(new { message = "API key is required for Google AI Studio." });
                var resp = await client.GetAsync(
                    $"https://generativelanguage.googleapis.com/v1beta/models?key={Uri.EscapeDataString(apiKey)}");
                if (!resp.IsSuccessStatusCode)
                    return StatusCode((int)resp.StatusCode, new { message = "Failed to reach Google AI Studio." });
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var models = doc.RootElement.GetProperty("models")
                    .EnumerateArray()
                    .Select(m => m.GetProperty("name").GetString() ?? "")
                    // Strip "models/" prefix and keep only Gemini generative models
                    .Select(n => n.StartsWith("models/") ? n["models/".Length..] : n)
                    .Where(n => n.StartsWith("gemini"))
                    .OrderBy(n => n)
                    .Select(n => new { name = n, size = 0L })
                    .ToList();
                return Ok(models);
            }

            if (string.Equals(provider, "OpenAI", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(apiKey))
                    return BadRequest(new { message = "API key is required for OpenAI." });
                var modelsUrl = string.IsNullOrWhiteSpace(endpoint)
                    ? "https://api.openai.com/v1/models"
                    : baseUrl + "/models";
                var request = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
                request.Headers.Add("Authorization", $"Bearer {apiKey}");
                var resp = await client.SendAsync(request);
                if (!resp.IsSuccessStatusCode)
                    return StatusCode((int)resp.StatusCode, new { message = "Failed to reach OpenAI." });
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var models = doc.RootElement.GetProperty("data")
                    .EnumerateArray()
                    .Select(m => new { name = m.GetProperty("id").GetString(), size = 0L })
                    .OrderBy(m => m.name)
                    .ToList();
                return Ok(models);
            }

            // Ollama (default) — no apiKey needed
            var ollamaResp = await client.GetAsync($"{baseUrl}/api/tags");
            if (!ollamaResp.IsSuccessStatusCode)
                return StatusCode((int)ollamaResp.StatusCode, new { message = "Failed to reach Ollama." });
            var ollamaJson = await ollamaResp.Content.ReadAsStringAsync();
            using var ollamaDoc = JsonDocument.Parse(ollamaJson);
            var ollamaModels = ollamaDoc.RootElement.GetProperty("models")
                .EnumerateArray()
                .Select(m => new
                {
                    name = m.GetProperty("name").GetString(),
                    size = m.TryGetProperty("size", out var s) ? s.GetInt64() : 0L
                })
                .OrderBy(m => m.name)
                .ToList();
            return Ok(ollamaModels);
        }
        catch (HttpRequestException)
        {
            return StatusCode(503, new { message = "LLM provider is not reachable." });
        }
    }

    [HttpPost("ollama/pull")]
    public async Task PullModel([FromBody] PullModelRequest req)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        var client = _httpClientFactory.CreateClient("ollama");
        var body = JsonSerializer.Serialize(new { name = req.Model, stream = true });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        try
        {
            using var resp = await client.PostAsync(
                $"{OllamaEndpoint}/api/pull", content, HttpContext.RequestAborted);

            await using var stream = await resp.Content.ReadAsStreamAsync(HttpContext.RequestAborted);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = await reader.ReadLineAsync(HttpContext.RequestAborted)) is not null
                   && !HttpContext.RequestAborted.IsCancellationRequested)
            {
                if (string.IsNullOrEmpty(line)) continue;
                await Response.WriteAsync($"data: {line}\n\n", HttpContext.RequestAborted);
                await Response.Body.FlushAsync(HttpContext.RequestAborted);
            }
        }
        catch (OperationCanceledException) { }
    }
}

public record PullModelRequest(string Model);
