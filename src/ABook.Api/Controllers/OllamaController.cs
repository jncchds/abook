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
    public async Task<IActionResult> GetModels()
    {
        try
        {
            var client = _httpClientFactory.CreateClient("ollama");
            var resp = await client.GetAsync($"{OllamaEndpoint}/api/tags");
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
            return StatusCode(503, new { message = "Ollama is not reachable." });
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
