using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ABook.Api.Controllers;

[ApiController]
[Route("api/configuration")]
[Authorize]
public class ConfigurationController : ControllerBase
{
    private readonly IBookRepository _repo;

    public ConfigurationController(IBookRepository repo) => _repo = repo;

    private int? CurrentUserId =>
        User.Identity?.IsAuthenticated == true
            ? int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!)
            : (int?)null;

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int? bookId) =>
        Ok(await _repo.GetLlmConfigAsync(bookId, CurrentUserId));

    [HttpPut]
    public async Task<IActionResult> Upsert([FromBody] LlmConfigRequest req)
    {
        var config = new LlmConfiguration
        {
            BookId = req.BookId,
            UserId = req.BookId.HasValue ? null : CurrentUserId,
            Provider = req.Provider,
            ModelName = req.ModelName,
            Endpoint = req.Endpoint,
            ApiKey = req.ApiKey,
            EmbeddingModelName = req.EmbeddingModelName,
            Temperature = req.Temperature ?? 0.8f,
            TimeoutMs = req.TimeoutMs,
            ReasoningEffort = req.ReasoningEffort,
            MaxTokens = req.MaxTokens
        };
        config.CopyImageSettingsFrom(req);
        var saved = await _repo.UpsertLlmConfigAsync(config);
        return Ok(saved);
    }

    /// <summary>
    /// Asks the image server whether it is up and its pipeline loaded ("Test connection"). A POST so the image
    /// API key travels in the body rather than the query string.
    /// </summary>
    [HttpPost("image-health")]
    public async Task<IActionResult> ImageHealth(
        [FromBody] ImageHealthRequest req, [FromServices] IImageGeneratorFactory factory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Endpoint)) return BadRequest(new { message = "Endpoint is required." });
        if (!Uri.TryCreate(req.Endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return BadRequest(new { message = "Endpoint must be an http(s) URL." });
        try
        {
            var health = await factory.Create(req.Provider).CheckHealthAsync(
                new LlmConfiguration
                {
                    ImageProvider = req.Provider,
                    ImageEndpoint = req.Endpoint,
                    ImageApiKey = req.ApiKey,
                    ImageModel = req.Model,
                }, ct);
            return Ok(health);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return Ok(new ImageGenHealth(false, "unreachable", ex.Message));
        }
    }
}

public record ImageHealthRequest(string Endpoint, ImageProvider? Provider, string? ApiKey, string? Model);

public record LlmConfigRequest(
    int? BookId,
    LlmProvider Provider,
    string ModelName,
    string Endpoint,
    string? ApiKey,
    string? EmbeddingModelName,
    float? Temperature = null,
    int? TimeoutMs = null,
    string? ReasoningEffort = null,
    int? MaxTokens = null,
    ImageProvider? ImageProvider = null,
    string? ImageEndpoint = null,
    string? ImageApiKey = null,
    string? ImageModel = null,
    int? ImageSteps = null,
    double? ImageCfg = null,
    int? ImagePortraitWidth = null,
    int? ImagePortraitHeight = null,
    int? ImageLandscapeWidth = null,
    int? ImageLandscapeHeight = null,
    int? ImageTimeoutMs = null,
    string? ImageVisualStyle = null,
    int? ImagesPerChapter = null,
    bool UnloadLlmBeforeRender = false) : IImageGenSettings;
