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

    public PresetsController(IBookRepository repo) => _repo = repo;

    private int? CurrentUserId =>
        User.Identity?.IsAuthenticated == true
            ? int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!)
            : (int?)null;

    private bool IsAdmin =>
        User.FindFirstValue(ClaimTypes.Role) == "Admin";

    [HttpGet]
    public async Task<IActionResult> GetAll() =>
        Ok(await _repo.GetPresetsAsync(CurrentUserId));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] PresetRequest req)
    {
        var preset = new LlmPreset
        {
            UserId = CurrentUserId,
            Name = req.Name,
            Provider = req.Provider,
            ModelName = req.ModelName,
            Endpoint = req.Endpoint,
            ApiKey = req.ApiKey,
            EmbeddingModelName = req.EmbeddingModelName,
        };
        var created = await _repo.CreatePresetAsync(preset);
        return CreatedAtAction(nameof(GetAll), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, [FromBody] PresetRequest req)
    {
        var existing = await _repo.GetPresetAsync(id);
        if (existing is null) return NotFound();

        // Only the owner or an admin may edit
        if (existing.UserId != CurrentUserId && !IsAdmin)
            return Forbid();

        existing.Name = req.Name;
        existing.Provider = req.Provider;
        existing.ModelName = req.ModelName;
        existing.Endpoint = req.Endpoint;
        existing.ApiKey = req.ApiKey;
        existing.EmbeddingModelName = req.EmbeddingModelName;
        await _repo.UpdatePresetAsync(existing);
        return Ok(existing);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var existing = await _repo.GetPresetAsync(id);
        if (existing is null) return NotFound();

        if (existing.UserId != CurrentUserId && !IsAdmin)
            return Forbid();

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
    string? EmbeddingModelName);
