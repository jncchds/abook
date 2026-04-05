using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;

namespace ABook.Api.Controllers;

[ApiController]
[Route("api/ollama")]
public class OllamaController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;

    public OllamaController(IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    private string OllamaEndpoint =>
        _config["Ollama:Endpoint"] ?? "http://host.docker.internal:11434";

    [HttpGet("models")]
    public async Task<IActionResult> GetModels([FromQuery] string? endpoint, [FromQuery] string? provider)
    {
        var baseUrl = (endpoint ?? OllamaEndpoint).TrimEnd('/');
        try
        {
            var client = _httpClientFactory.CreateClient("ollama");

            // LMStudio (and other OpenAI-compatible servers) expose GET /v1/models
            if (string.Equals(provider, "LMStudio", StringComparison.OrdinalIgnoreCase))
            {
                var v1Url = baseUrl.TrimEnd('/');
                if (!v1Url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                    v1Url += "/v1";
                var resp2 = await client.GetAsync($"{v1Url}/models");
                if (!resp2.IsSuccessStatusCode)
                    return StatusCode((int)resp2.StatusCode, new { message = "Failed to reach LM Studio." });
                var json2 = await resp2.Content.ReadAsStringAsync();
                using var doc2 = JsonDocument.Parse(json2);
                var models2 = doc2.RootElement.GetProperty("data")
                    .EnumerateArray()
                    .Select(m => new { name = m.GetProperty("id").GetString(), size = 0L })
                    .OrderBy(m => m.name)
                    .ToList();
                return Ok(models2);
            }

            var resp = await client.GetAsync($"{baseUrl}/api/tags");
            if (!resp.IsSuccessStatusCode)
                return StatusCode((int)resp.StatusCode, new { message = "Failed to reach Ollama." });

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var models = doc.RootElement.GetProperty("models")
                .EnumerateArray()
                .Select(m => new
                {
                    name = m.GetProperty("name").GetString(),
                    size = m.TryGetProperty("size", out var s) ? s.GetInt64() : 0L
                })
                .OrderBy(m => m.name)
                .ToList();
            return Ok(models);
        }
        catch (HttpRequestException)
        {
            return StatusCode(503, new { message = "LLM provider is not reachable." });
        }
    }

    [HttpPost("pull")]
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
