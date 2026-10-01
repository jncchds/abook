using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ABook.Api.Controllers;

[ApiController]
[Route("api/presets")]
[Authorize]
public class PresetsController : ControllerBase
{
    private readonly IBookRepository _repo;
    private readonly IUserRepository _users;

    public PresetsController(IBookRepository repo, IUserRepository users)
    {
        _repo = repo;
        _users = users;
    }

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private bool IsAdmin =>
        User.FindFirstValue(ClaimTypes.Role) == "Admin";

    /// <summary>Shared presets (UserId null) are usable by everyone but only admins may change them.</summary>
    private bool CanModify(LlmPreset preset) => preset.UserId == CurrentUserId || (preset.UserId is null && IsAdmin);

    [HttpGet]
    public async Task<IActionResult> GetAll() =>
        Ok(await _repo.GetPresetsAsync(CurrentUserId));

    /// <summary>The preset copied into each new book; null when the user has none.</summary>
    [HttpGet("default")]
    public async Task<IActionResult> GetDefault() =>
        Ok(new { presetId = (await _repo.GetDefaultPresetAsync(CurrentUserId))?.Id });

    [HttpPut("{id}/default")]
    public async Task<IActionResult> SetDefault(int id)
    {
        var preset = await _repo.GetPresetAsync(id);
        if (preset is null || (preset.UserId is not null && preset.UserId != CurrentUserId)) return NotFound();
        var user = await _users.GetByIdAsync(CurrentUserId);
        if (user is null) return Unauthorized();
        user.DefaultPresetId = id;
        await _users.UpdateAsync(user);
        return Ok(new { presetId = id });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] PresetRequest req)
    {
        if (req.Shared && !IsAdmin) return Forbid();
        var preset = new LlmPreset
        {
            UserId = req.Shared ? null : CurrentUserId,
            Name = req.Name,
            Provider = req.Provider,
            ModelName = req.ModelName,
            Endpoint = req.Endpoint,
            ApiKey = req.ApiKey,
            EmbeddingModelName = req.EmbeddingModelName,
            Temperature = req.Temperature ?? 0.8f,
            TimeoutMs = req.TimeoutMs,
            ReasoningEffort = req.ReasoningEffort,
            MaxTokens = req.MaxTokens,
        };
        preset.CopyImageSettingsFrom(req);
        var created = await _repo.CreatePresetAsync(preset);
        return CreatedAtAction(nameof(GetAll), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, [FromBody] PresetRequest req)
    {
        var existing = await _repo.GetPresetAsync(id);
        if (existing is null) return NotFound();

        if (!CanModify(existing)) return Forbid();

        // An admin may share their own preset. Sharing is one-way: a shared preset is never taken private,
        // since other users may have made it their default.
        if (req.Shared && existing.UserId is not null)
        {
            if (!IsAdmin) return Forbid();
            existing.UserId = null;
        }

        existing.Name = req.Name;
        existing.Provider = req.Provider;
        existing.ModelName = req.ModelName;
        existing.Endpoint = req.Endpoint;
        existing.ApiKey = req.ApiKey;
        existing.EmbeddingModelName = req.EmbeddingModelName;
        existing.Temperature = req.Temperature ?? 0.8f;
        existing.TimeoutMs = req.TimeoutMs;
        existing.ReasoningEffort = req.ReasoningEffort;
        existing.MaxTokens = req.MaxTokens;
        existing.CopyImageSettingsFrom(req);
        await _repo.UpdatePresetAsync(existing);
        return Ok(existing);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var existing = await _repo.GetPresetAsync(id);
        if (existing is null) return NotFound();

        // Users who had it as their default are left without one (FK set null) and are asked to pick another.
        if (!CanModify(existing)) return Forbid();

        await _repo.DeletePresetAsync(id);
        return NoContent();
    }
}

public record PresetRequest(
    string Name,
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
    bool UnloadLlmBeforeRender = false,
    bool Shared = false) : IImageGenSettings;
