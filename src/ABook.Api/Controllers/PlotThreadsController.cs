using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ABook.Api.Controllers;

[ApiController]
[Route("api/books/{bookId:int}/plot-threads")]
[Authorize]
public class PlotThreadsController : ControllerBase
{
    private readonly IBookRepository _repo;

    public PlotThreadsController(IBookRepository repo) => _repo = repo;

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
    public async Task<IActionResult> GetAll(int bookId, [FromQuery] bool includeArchived = false) =>
        Ok(includeArchived
            ? await _repo.GetAllPlotThreadsAsync(bookId)
            : await _repo.GetPlotThreadsAsync(bookId));

    [HttpGet("{threadId:int}")]
    public async Task<IActionResult> GetById(int bookId, int threadId)
    {
        var thread = await _repo.GetPlotThreadAsync(bookId, threadId);
        return thread is null ? NotFound() : Ok(thread);
    }

    [HttpPost]
    public async Task<IActionResult> Create(int bookId, [FromBody] PlotThreadRequest req)
    {
        var ownershipError = await CheckOwnershipAsync(bookId);
        if (ownershipError is not null) return ownershipError;

        var thread = new PlotThread
        {
            BookId = bookId,
            Name = req.Name,
            Description = req.Description,
            Type = req.Type,
            IntroducedChapterNumber = req.IntroducedChapterNumber,
            ResolvedChapterNumber = req.ResolvedChapterNumber,
            Status = req.Status
        };
        var created = await _repo.AddPlotThreadAsync(thread);
        await _repo.AddPlotThreadVersionAsync(new PlotThreadVersion
        {
            PlotThreadId = created.Id,
            BookId = bookId,
            Name = created.Name,
            Description = created.Description,
            Type = created.Type,
            IntroducedChapterNumber = created.IntroducedChapterNumber,
            ResolvedChapterNumber = created.ResolvedChapterNumber,
            Status = created.Status,
            CreatedBy = "user",
        });
        return CreatedAtAction(nameof(GetById), new { bookId, threadId = created.Id }, created);
    }

    [HttpPut("{threadId:int}")]
    public async Task<IActionResult> Update(int bookId, int threadId, [FromBody] PlotThreadRequest req)
    {
        var ownershipError = await CheckOwnershipAsync(bookId);
        if (ownershipError is not null) return ownershipError;

        var thread = await _repo.GetPlotThreadAsync(bookId, threadId);
        if (thread is null) return NotFound();
        await _repo.AddPlotThreadVersionAsync(new PlotThreadVersion
        {
            PlotThreadId = threadId,
            BookId = bookId,
            Name = req.Name,
            Description = req.Description,
            Type = req.Type,
            IntroducedChapterNumber = req.IntroducedChapterNumber,
            ResolvedChapterNumber = req.ResolvedChapterNumber,
            Status = req.Status,
            CreatedBy = "user",
        });
        thread.Name = req.Name;
        thread.Description = req.Description;
        thread.Type = req.Type;
        thread.IntroducedChapterNumber = req.IntroducedChapterNumber;
        thread.ResolvedChapterNumber = req.ResolvedChapterNumber;
        thread.Status = req.Status;
        await _repo.UpdatePlotThreadAsync(thread);
        return Ok(thread);
    }

    [HttpPost("{threadId:int}/archive")]
    public async Task<IActionResult> Archive(int bookId, int threadId)
    {
        var ownershipError = await CheckOwnershipAsync(bookId);
        if (ownershipError is not null) return ownershipError;

        var thread = await _repo.GetPlotThreadAsync(bookId, threadId);
        if (thread is null) return NotFound();
        await _repo.AddPlotThreadVersionAsync(new PlotThreadVersion
        {
            PlotThreadId = threadId,
            BookId = bookId,
            Name = thread.Name,
            Description = thread.Description,
            Type = thread.Type,
            IntroducedChapterNumber = thread.IntroducedChapterNumber,
            ResolvedChapterNumber = thread.ResolvedChapterNumber,
            Status = thread.Status,
            CreatedBy = "archived",
        });
        await _repo.ArchivePlotThreadAsync(bookId, threadId);
        return NoContent();
    }

    [HttpPost("{threadId:int}/unarchive")]
    public async Task<IActionResult> Unarchive(int bookId, int threadId)
    {
        var ownershipError = await CheckOwnershipAsync(bookId);
        if (ownershipError is not null) return ownershipError;

        var thread = await _repo.GetPlotThreadAsync(bookId, threadId);
        if (thread is null) return NotFound();
        await _repo.UnarchivePlotThreadAsync(bookId, threadId);
        return Ok(await _repo.GetPlotThreadAsync(bookId, threadId));
    }

    [HttpDelete("{threadId:int}")]
    public async Task<IActionResult> Delete(int bookId, int threadId)
    {
        var ownershipError = await CheckOwnershipAsync(bookId);
        if (ownershipError is not null) return ownershipError;

        await _repo.DeletePlotThreadAsync(bookId, threadId);
        return NoContent();
    }

    [HttpGet("{threadId:int}/history")]
    public async Task<IActionResult> GetItemHistory(int bookId, int threadId)
    {
        var thread = await _repo.GetPlotThreadAsync(bookId, threadId);
        if (thread is null) return NotFound();
        return Ok(await _repo.GetPlotThreadVersionsAsync(bookId, threadId));
    }

    [HttpGet("{threadId:int}/history/{versionId:int}")]
    public async Task<IActionResult> GetItemVersion(int bookId, int threadId, int versionId)
    {
        var version = await _repo.GetPlotThreadVersionAsync(bookId, threadId, versionId);
        return version is null ? NotFound() : Ok(version);
    }

    [HttpPost("{threadId:int}/history/{versionId:int}/restore")]
    public async Task<IActionResult> RestoreItemVersion(int bookId, int threadId, int versionId)
    {
        var ownershipError = await CheckOwnershipAsync(bookId);
        if (ownershipError is not null) return ownershipError;

        try { return Ok(await _repo.RestorePlotThreadVersionAsync(bookId, threadId, versionId)); }
        catch (InvalidOperationException) { return NotFound(); }
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory(int bookId) =>
        Ok(await _repo.GetPlotThreadsSnapshotsAsync(bookId));

    [HttpGet("history/{snapshotId:int}")]
    public async Task<IActionResult> GetSnapshot(int bookId, int snapshotId)
    {
        var snapshot = await _repo.GetPlotThreadsSnapshotAsync(bookId, snapshotId);
        return snapshot is null ? NotFound() : Ok(snapshot);
    }

    [HttpPost("history/{snapshotId:int}/restore")]
    public async Task<IActionResult> RestoreSnapshot(int bookId, int snapshotId)
    {
        var ownershipError = await CheckOwnershipAsync(bookId);
        if (ownershipError is not null) return ownershipError;

        try { return Ok(await _repo.RestorePlotThreadsSnapshotAsync(bookId, snapshotId)); }
        catch (InvalidOperationException) { return NotFound(); }
    }
}

public record PlotThreadRequest(
    string Name,
    string Description,
    PlotThreadType Type,
    int? IntroducedChapterNumber,
    int? ResolvedChapterNumber,
    PlotThreadStatus Status);
