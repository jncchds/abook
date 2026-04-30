using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace ABook.Api.Controllers;

[ApiController]
[Route("api/books/{bookId:int}/plot-threads")]
public class PlotThreadsController : ControllerBase
{
    private readonly IBookRepository _repo;

    public PlotThreadsController(IBookRepository repo) => _repo = repo;

    [HttpGet]
    public async Task<IActionResult> GetAll(int bookId) =>
        Ok(await _repo.GetPlotThreadsAsync(bookId));

    [HttpGet("{threadId:int}")]
    public async Task<IActionResult> GetById(int bookId, int threadId)
    {
        var thread = await _repo.GetPlotThreadAsync(bookId, threadId);
        return thread is null ? NotFound() : Ok(thread);
    }

    [HttpPost]
    public async Task<IActionResult> Create(int bookId, [FromBody] PlotThreadRequest req)
    {
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
        return CreatedAtAction(nameof(GetById), new { bookId, threadId = created.Id }, created);
    }

    [HttpPut("{threadId:int}")]
    public async Task<IActionResult> Update(int bookId, int threadId, [FromBody] PlotThreadRequest req)
    {
        var thread = await _repo.GetPlotThreadAsync(bookId, threadId);
        if (thread is null) return NotFound();
        thread.Name = req.Name;
        thread.Description = req.Description;
        thread.Type = req.Type;
        thread.IntroducedChapterNumber = req.IntroducedChapterNumber;
        thread.ResolvedChapterNumber = req.ResolvedChapterNumber;
        thread.Status = req.Status;
        await _repo.UpdatePlotThreadAsync(thread);
        return Ok(thread);
    }

    [HttpDelete("{threadId:int}")]
    public async Task<IActionResult> Delete(int bookId, int threadId)
    {
        await _repo.DeletePlotThreadAsync(bookId, threadId);
        return NoContent();
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
}

public record PlotThreadRequest(
    string Name,
    string Description,
    PlotThreadType Type,
    int? IntroducedChapterNumber,
    int? ResolvedChapterNumber,
    PlotThreadStatus Status);
