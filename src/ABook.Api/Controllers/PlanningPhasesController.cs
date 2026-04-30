using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace ABook.Api.Controllers;

/// <summary>
/// Manages the planning phase completion status for Story Bible, Characters, Plot Threads, and Chapters.
/// Supports three actions per phase:
///   POST /{phase}/complete  — mark phase as Complete (data kept)
///   POST /{phase}/reopen    — reset phase to NotStarted (data kept)
///   DELETE /{phase}         — delete phase data AND reset to NotStarted
/// Valid phase values: storybible | characters | plotthreads | chapters
/// </summary>
[ApiController]
[Route("api/books/{bookId:int}/planning-phases")]
public class PlanningPhasesController : ControllerBase
{
    private readonly IBookRepository _repo;

    public PlanningPhasesController(IBookRepository repo) => _repo = repo;

    [HttpPost("{phase}/complete")]
    public async Task<IActionResult> Complete(int bookId, string phase)
    {
        var book = await _repo.GetByIdAsync(bookId);
        if (book is null) return NotFound();
        if (!ApplyStatus(book, phase, PlanningPhaseStatus.Complete))
            return BadRequest(new { message = $"Unknown planning phase: {phase}" });
        await _repo.UpdateAsync(book);
        return Ok();
    }

    [HttpPost("{phase}/reopen")]
    public async Task<IActionResult> Reopen(int bookId, string phase)
    {
        var book = await _repo.GetByIdAsync(bookId);
        if (book is null) return NotFound();
        if (!ApplyStatus(book, phase, PlanningPhaseStatus.NotStarted))
            return BadRequest(new { message = $"Unknown planning phase: {phase}" });
        await _repo.UpdateAsync(book);
        return Ok();
    }

    [HttpDelete("{phase}")]
    public async Task<IActionResult> Clear(int bookId, string phase)
    {
        var book = await _repo.GetByIdAsync(bookId);
        if (book is null) return NotFound();

        switch (phase.ToLowerInvariant())
        {
            case "storybible":
                {
                    var existing = await _repo.GetStoryBibleAsync(bookId);
                    if (existing is not null)
                    {
                        await _repo.AddStoryBibleSnapshotAsync(new ABook.Core.Models.StoryBibleSnapshot
                        {
                            BookId = bookId,
                            SettingDescription = existing.SettingDescription,
                            TimePeriod = existing.TimePeriod,
                            Themes = existing.Themes,
                            ToneAndStyle = existing.ToneAndStyle,
                            WorldRules = existing.WorldRules,
                            Notes = existing.Notes,
                            Reason = "planning-phase-reset"
                        });
                    }
                    await _repo.DeleteStoryBibleAsync(bookId);
                    book.StoryBibleStatus = PlanningPhaseStatus.NotStarted;
                    break;
                }
            case "characters":
                {
                    var existing = await _repo.GetCharacterCardsAsync(bookId);
                    if (existing.Any())
                    {
                        var serialized = System.Text.Json.JsonSerializer.Serialize(existing);
                        await _repo.AddCharactersSnapshotAsync(new ABook.Core.Models.CharactersSnapshot
                        {
                            BookId = bookId,
                            DataJson = serialized,
                            Reason = "planning-phase-reset"
                        });
                    }
                    await _repo.DeleteCharacterCardsAsync(bookId);
                    book.CharactersStatus = PlanningPhaseStatus.NotStarted;
                    break;
                }
            case "plotthreads":
                {
                    var existing = await _repo.GetPlotThreadsAsync(bookId);
                    if (existing.Any())
                    {
                        var serialized = System.Text.Json.JsonSerializer.Serialize(existing);
                        await _repo.AddPlotThreadsSnapshotAsync(new ABook.Core.Models.PlotThreadsSnapshot
                        {
                            BookId = bookId,
                            DataJson = serialized,
                            Reason = "planning-phase-reset"
                        });
                    }
                    await _repo.DeletePlotThreadsAsync(bookId);
                    book.PlotThreadsStatus = PlanningPhaseStatus.NotStarted;
                    break;
                }
            case "chapters":
                // Archive chapters instead of hard-delete to preserve history
                await _repo.ArchiveChaptersAsync(bookId);
                book.ChaptersStatus = PlanningPhaseStatus.NotStarted;
                break;
            default:
                return BadRequest(new { message = $"Unknown planning phase: {phase}" });
        }

        await _repo.UpdateAsync(book);
        return Ok();
    }

    private static bool ApplyStatus(Book book, string phase, PlanningPhaseStatus status)
    {
        switch (phase.ToLowerInvariant())
        {
            case "storybible":   book.StoryBibleStatus   = status; return true;
            case "characters":   book.CharactersStatus   = status; return true;
            case "plotthreads":  book.PlotThreadsStatus  = status; return true;
            case "chapters":     book.ChaptersStatus     = status; return true;
            default:             return false;
        }
    }
}
