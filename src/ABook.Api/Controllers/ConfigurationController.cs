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
        var saved = await _repo.UpsertLlmConfigAsync(config);
        return Ok(saved);
    }
}

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
    int? MaxTokens = null);
