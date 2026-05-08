using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ABook.Api.Controllers;

[ApiController]
[Route("api/books/{bookId:int}/story-bible")]
[Authorize]
public class StoryBibleController : ControllerBase
{
    private readonly IBookRepository _repo;

    public StoryBibleController(IBookRepository repo) => _repo = repo;

    private int? CurrentUserId =>
        User.Identity?.IsAuthenticated == true
            ? int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!)
            : (int?)null;

    private async Task<IActionResult?> CheckOwnershipAsync(int bookId)
    {
        var book = await _repo.GetByIdAsync(bookId);
        if (book is null) return NotFound();
        if (book.UserId is not null && book.UserId != CurrentUserId) return Forbid();
        return null;
    }

    [HttpGet]
    public async Task<IActionResult> Get(int bookId)
    {
        var bible = await _repo.GetStoryBibleAsync(bookId);
        return bible is null ? Ok(new StoryBible { BookId = bookId }) : Ok(bible);
    }

    [HttpPut]
    public async Task<IActionResult> Upsert(int bookId, [FromBody] StoryBibleRequest req)
    {
        var ownershipError = await CheckOwnershipAsync(bookId);
        if (ownershipError is not null) return ownershipError;

        // Snapshot the existing bible before overwriting
        var existing = await _repo.GetStoryBibleAsync(bookId);
        if (existing is not null)
        {
            await _repo.AddStoryBibleSnapshotAsync(new StoryBibleSnapshot
            {
                BookId = bookId,
                SettingDescription = existing.SettingDescription,
                TimePeriod = existing.TimePeriod,
                Themes = existing.Themes,
                ToneAndStyle = existing.ToneAndStyle,
                WorldRules = existing.WorldRules,
                Notes = existing.Notes,
                Reason = "user-update"
            });
        }
        var bible = new StoryBible
        {
            BookId = bookId,
            SettingDescription = req.SettingDescription,
            TimePeriod = req.TimePeriod,
            Themes = req.Themes,
            ToneAndStyle = req.ToneAndStyle,
            WorldRules = req.WorldRules,
            Notes = req.Notes
        };
        var result = await _repo.UpsertStoryBibleAsync(bible);
        return Ok(result);
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory(int bookId) =>
        Ok(await _repo.GetStoryBibleSnapshotsAsync(bookId));

    [HttpGet("history/{snapshotId:int}")]
    public async Task<IActionResult> GetSnapshot(int bookId, int snapshotId)
    {
        var snapshot = await _repo.GetStoryBibleSnapshotAsync(bookId, snapshotId);
        return snapshot is null ? NotFound() : Ok(snapshot);
    }

    [HttpPost("history/{snapshotId:int}/restore")]
    public async Task<IActionResult> RestoreSnapshot(int bookId, int snapshotId)
    {
        var ownershipError = await CheckOwnershipAsync(bookId);
        if (ownershipError is not null) return ownershipError;

        try
        {
            var restored = await _repo.RestoreStoryBibleSnapshotAsync(bookId, snapshotId);
            return Ok(restored);
        }
        catch (InvalidOperationException) { return NotFound(); }
    }
}

public record StoryBibleRequest(
    string SettingDescription,
    string TimePeriod,
    string Themes,
    string ToneAndStyle,
    string WorldRules,
    string Notes);
