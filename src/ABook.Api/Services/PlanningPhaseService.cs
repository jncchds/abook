using ABook.Agents;
using ABook.Core.Interfaces;
using ABook.Core.Models;

namespace ABook.Api.Services;

/// <summary>
/// The planning phases' status, set by hand from the web UI (<c>PlanningPhasesController</c>) and MCP: complete (data
/// kept), reopen (back to NotStarted, data kept) and clear (data snapshotted where it has a history, then removed or
/// archived, and NotStarted). Phases: clarifications | storybible | characters | plotthreads | chapters — the last
/// one is the plan of the book: its sections and their chapters.
/// </summary>
public class PlanningPhaseService(IBookRepository repo, ClarificationService clarifications)
{
    public static readonly IReadOnlyList<string> Phases = ["clarifications", "storybible", "characters", "plotthreads", "chapters"];

    /// <summary>Complete or reopen a phase. False for an unknown phase.</summary>
    public async Task<bool> SetStatusAsync(Book book, string phase, PlanningPhaseStatus status)
    {
        if (!ApplyStatus(book, phase, status)) return false;
        await repo.UpdateAsync(book);
        // Marking the round complete by hand releases a run still waiting for answers.
        if (status == PlanningPhaseStatus.Complete && phase.Equals("clarifications", StringComparison.OrdinalIgnoreCase))
            clarifications.ReleaseWaitingRun(book.Id);
        return true;
    }

    /// <summary>
    /// Clears a phase's data and resets it to NotStarted. Throws <see cref="ArgumentException"/> for an unknown phase
    /// and <see cref="StoryBibleLockedException"/> for the Story Bible of a book that is being written.
    /// </summary>
    public async Task ClearAsync(Book book, string phase)
    {
        var bookId = book.Id;
        switch (phase.ToLowerInvariant())
        {
            case "clarifications":
                // Archived, not deleted — the answers stay readable on the Clarifications page. A run
                // waiting for answers carries on without them; the next run asks again.
                await clarifications.ArchiveAllAsync(bookId);
                book.ClarificationsStatus = PlanningPhaseStatus.NotStarted;
                break;
            case "storybible":
            {
                if (BookContentService.IsBibleLocked(book)) throw new StoryBibleLockedException();
                var existing = await repo.GetStoryBibleAsync(bookId);
                if (existing is not null)
                {
                    await repo.AddStoryBibleSnapshotAsync(new StoryBibleSnapshot
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
                await repo.DeleteStoryBibleAsync(bookId);
                book.StoryBibleStatus = PlanningPhaseStatus.NotStarted;
                break;
            }
            case "characters":
            {
                var existing = await repo.GetCharacterCardsAsync(bookId);
                if (existing.Any())
                    await repo.AddCharactersSnapshotAsync(new CharactersSnapshot
                    {
                        BookId = bookId,
                        DataJson = System.Text.Json.JsonSerializer.Serialize(existing),
                        Reason = "planning-phase-reset"
                    });
                await repo.DeleteCharacterCardsAsync(bookId);
                book.CharactersStatus = PlanningPhaseStatus.NotStarted;
                break;
            }
            case "plotthreads":
            {
                var existing = await repo.GetPlotThreadsAsync(bookId);
                if (existing.Any())
                    await repo.AddPlotThreadsSnapshotAsync(new PlotThreadsSnapshot
                    {
                        BookId = bookId,
                        DataJson = System.Text.Json.JsonSerializer.Serialize(existing),
                        Reason = "planning-phase-reset"
                    });
                await repo.DeletePlotThreadsAsync(bookId);
                book.PlotThreadsStatus = PlanningPhaseStatus.NotStarted;
                break;
            }
            case "chapters":
                // Archived, not deleted, to keep their history; their sections go with them (a section without an
                // active chapter is archived).
                await repo.ArchiveChaptersAsync(bookId);
                book.ChaptersStatus = PlanningPhaseStatus.NotStarted;
                break;
            default:
                throw new ArgumentException($"Unknown planning phase: {phase}. Valid: {string.Join(", ", Phases)}.");
        }
        await repo.UpdateAsync(book);
    }

    private static bool ApplyStatus(Book book, string phase, PlanningPhaseStatus status)
    {
        switch (phase.ToLowerInvariant())
        {
            case "clarifications": book.ClarificationsStatus = status; return true;
            case "storybible":     book.StoryBibleStatus     = status; return true;
            case "characters":     book.CharactersStatus     = status; return true;
            case "plotthreads":    book.PlotThreadsStatus    = status; return true;
            case "chapters":       book.ChaptersStatus       = status; return true;
            default:               return false;
        }
    }
}
