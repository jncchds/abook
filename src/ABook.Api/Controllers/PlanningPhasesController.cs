using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ABook.Api.Controllers;

/// <summary>
/// Manages the planning phase completion status for Clarifications, Story Bible, Characters, Plot Threads, and Chapters.
/// Supports three actions per phase:
///   POST /{phase}/complete  — mark phase as Complete (data kept)
///   POST /{phase}/reopen    — reset phase to NotStarted (data kept)
///   DELETE /{phase}         — delete phase data AND reset to NotStarted
/// Valid phase values: clarifications | storybible | characters | plotthreads | chapters
/// </summary>
[ApiController]
[Route("api/books/{bookId:int}/planning-phases")]
[Authorize]
public class PlanningPhasesController : ControllerBase
{
    private readonly IBookRepository _repo;
    private readonly ABook.Api.Services.ClarificationService _clarifications;

    public PlanningPhasesController(IBookRepository repo, ABook.Api.Services.ClarificationService clarifications)
    {
        _repo = repo;
        _clarifications = clarifications;
    }

    [HttpPost("{phase}/complete")]
    public Task<IActionResult> Complete(int bookId, string phase) =>
        SetPhaseStatusAsync(bookId, phase, PlanningPhaseStatus.Complete);

    [HttpPost("{phase}/reopen")]
    public Task<IActionResult> Reopen(int bookId, string phase) =>
        SetPhaseStatusAsync(bookId, phase, PlanningPhaseStatus.NotStarted);

    private async Task<IActionResult> SetPhaseStatusAsync(int bookId, string phase, PlanningPhaseStatus status)
    {
        var (error, book) = await this.RequireBookOwnershipAndLoadAsync(bookId, _repo);
        if (error is not null) return error;
        if (!ApplyStatus(book!, phase, status))
            return BadRequest(new { message = $"Unknown planning phase: {phase}" });
        await _repo.UpdateAsync(book!);
        // Marking the round complete by hand releases a run still waiting for answers.
        if (status == PlanningPhaseStatus.Complete && phase.Equals("clarifications", StringComparison.OrdinalIgnoreCase))
            _clarifications.ReleaseWaitingRun(bookId);
        return Ok();
    }

    [HttpDelete("{phase}")]
    public async Task<IActionResult> Clear(int bookId, string phase)
    {
        var (error, book) = await this.RequireBookOwnershipAndLoadAsync(bookId, _repo);
        if (error is not null) return error;

        switch (phase.ToLowerInvariant())
        {
            case "clarifications":
                // Archived, not deleted — the answers stay readable on the Clarifications page. A run
                // waiting for answers carries on without them; the next run asks again.
                await _clarifications.ArchiveAllAsync(bookId);
                book!.ClarificationsStatus = PlanningPhaseStatus.NotStarted;
                break;
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
                    book!.StoryBibleStatus = PlanningPhaseStatus.NotStarted;
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
                    book!.CharactersStatus = PlanningPhaseStatus.NotStarted;
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
                    book!.PlotThreadsStatus = PlanningPhaseStatus.NotStarted;
                    break;
                }
            case "chapters":
                // Archive chapters instead of hard-delete to preserve history
                await _repo.ArchiveChaptersAsync(bookId);
                book!.ChaptersStatus = PlanningPhaseStatus.NotStarted;
                break;
            default:
                return BadRequest(new { message = $"Unknown planning phase: {phase}" });
        }

        await _repo.UpdateAsync(book!);
        return Ok();
    }

    private static bool ApplyStatus(Book book, string phase, PlanningPhaseStatus status)
    {
        switch (phase.ToLowerInvariant())
        {
            case "clarifications": book.ClarificationsStatus = status; return true;
            case "storybible":   book.StoryBibleStatus   = status; return true;
            case "characters":   book.CharactersStatus   = status; return true;
            case "plotthreads":  book.PlotThreadsStatus  = status; return true;
            case "chapters":     book.ChaptersStatus     = status; return true;
            default:             return false;
        }
    }
}
