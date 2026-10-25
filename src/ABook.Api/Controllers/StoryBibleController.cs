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
    private readonly ABook.Agents.BookContentService _content;

    public StoryBibleController(IBookRepository repo, ABook.Agents.BookContentService content)
    {
        _repo = repo;
        _content = content;
    }

    [HttpGet]
    public async Task<IActionResult> Get(int bookId)
    {
        var bible = await _repo.GetStoryBibleAsync(bookId);
        return bible is null ? Ok(new StoryBible { BookId = bookId }) : Ok(bible);
    }

    /// <summary>409 when the book is an Interactive book whose premise and Story Bible are locked.</summary>
    private async Task<IActionResult?> RefuseIfLockedAsync(int bookId) =>
        await _repo.GetByIdAsync(bookId) is { } book && ABook.Agents.BookContentService.IsBibleLocked(book)
            ? Conflict(new { message = new ABook.Agents.StoryBibleLockedException().Message })
            : null;

    [HttpPut]
    public async Task<IActionResult> Upsert(int bookId, [FromBody] StoryBibleRequest req)
    {
        var ownershipError = await ControllerExtensions.RequireBookOwnershipAsync(this, bookId, _repo);
        if (ownershipError is not null) return ownershipError;
        if (await RefuseIfLockedAsync(bookId) is { } locked) return locked;

        // Saved like every bible edit, with its history (EditSources: edits on top of the author's own amend it).
        await _content.UpdateStoryBibleAsync(bookId, new Dictionary<string, string>
        {
            ["settingDescription"] = req.SettingDescription,
            ["timePeriod"] = req.TimePeriod,
            ["themes"] = req.Themes,
            ["toneAndStyle"] = req.ToneAndStyle,
            ["worldRules"] = req.WorldRules,
            ["notes"] = req.Notes,
        }, source: EditSources.UserEdit);
        return Ok(await _repo.GetStoryBibleAsync(bookId));
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
        var ownershipError = await ControllerExtensions.RequireBookOwnershipAsync(this, bookId, _repo);
        if (ownershipError is not null) return ownershipError;
        if (await RefuseIfLockedAsync(bookId) is { } locked) return locked;

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
