using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace ABook.Api.Controllers;

/// <summary>
/// Lists provider models and pulls Ollama models. Signed-in users only: both make the server issue
/// requests (the model list to a caller-supplied endpoint, the pull a potentially huge download).
/// </summary>
[ApiController]
[Route("api")]
[Authorize]
public class ModelsController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<ModelsController> _logger;

    public ModelsController(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<ModelsController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    private string OllamaEndpoint =>
        _config["Ollama:Endpoint"] ?? "http://host.docker.internal:11434";

    /// <remarks>
    /// The API key is read from the <c>X-Api-Key</c> header so it stays out of URLs and access logs;
    /// the <c>apiKey</c> query parameter is still accepted for older clients.
    /// </remarks>
    [HttpGet("models")]
    public async Task<IActionResult> GetModels(
        [FromQuery] string? endpoint,
        [FromQuery] string? provider,
        [FromHeader(Name = "X-Api-Key")] string? apiKeyHeader,
        [FromQuery(Name = "apiKey")] string? apiKeyQuery)
    {
        var apiKey = string.IsNullOrWhiteSpace(apiKeyHeader) ? apiKeyQuery : apiKeyHeader;
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
                    return UpstreamError(resp, "Google AI Studio");
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
                var modelsUrl = string.IsNullOrWhiteSpace(endpoint)
                    ? "https://api.openai.com/v1/models"
                    : baseUrl + "/models";
                var request = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
                if (!string.IsNullOrWhiteSpace(apiKey))
                    request.Headers.Add("Authorization", $"Bearer {apiKey}");
                var resp = await client.SendAsync(request);
                if (!resp.IsSuccessStatusCode)
                    return UpstreamError(resp, "OpenAI");
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var models = doc.RootElement.GetProperty("data")
                    .EnumerateArray()
                    .Select(m => new { name = m.GetProperty("id").GetString(), size = 0L })
                    .OrderBy(m => m.name)
                    .ToList();
                return Ok(models);
            }

            if (string.Equals(provider, "OpenAICompatible", StringComparison.OrdinalIgnoreCase))
            {
                var modelsUrl = baseUrl + "/models";
                var request = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
                if (!string.IsNullOrWhiteSpace(apiKey))
                    request.Headers.Add("Authorization", $"Bearer {apiKey}");
                var resp = await client.SendAsync(request);
                if (!resp.IsSuccessStatusCode)
                    return UpstreamError(resp, "The OpenAI-compatible endpoint");
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
                return UpstreamError(ollamaResp, "Ollama");
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
        catch (TaskCanceledException)
        {
            return StatusCode(504, new { message = "LLM provider did not respond in time." });
        }
        catch (Exception ex) when (ex is UriFormatException or InvalidOperationException && !Uri.IsWellFormedUriString(baseUrl, UriKind.Absolute))
        {
            return BadRequest(new { message = $"'{baseUrl}' is not a valid endpoint URL." });
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Unexpected model list response from {Provider} at {Endpoint}", provider, baseUrl);
            return StatusCode(502, new { message = "LLM provider returned an unexpected response — check the endpoint (OpenAI-compatible endpoints usually end in /v1)." });
        }
    }

    /// <summary>
    /// Reports a failed provider call as 502. The provider's own status must not be passed through: its 401
    /// (a missing or wrong API key) would look like our session expiring and sign the user out.
    /// </summary>
    private ObjectResult UpstreamError(HttpResponseMessage resp, string providerName)
    {
        var status = (int)resp.StatusCode;
        var hint = status is 401 or 403 ? " — check the API key" : status == 404 ? " — check the endpoint URL" : "";
        return StatusCode(502, new { message = $"{providerName} returned {status} {resp.ReasonPhrase}{hint}." });
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
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Ollama pull cancelled by client.");
        }
    }
}

public record PullModelRequest(string Model);
