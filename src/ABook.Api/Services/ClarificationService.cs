using ABook.Agents;
using ABook.Core.Interfaces;
using ABook.Core.Models;

namespace ABook.Api.Services;

/// <summary>
/// Edits clarifications on behalf of the author (UI and MCP). Answering or archiving the last open
/// question closes the clarification round: the phase is marked Complete and a planning run waiting
/// for the answers carries on.
/// </summary>
public class ClarificationService
{
    private readonly IBookRepository _repo;
    private readonly AgentRunStateService _state;

    public ClarificationService(IBookRepository repo, AgentRunStateService state)
    {
        _repo = repo;
        _state = state;
    }

    /// <summary>
    /// Applies the given changes (null = keep). Blank text is the caller's to reject. <paramref name="origin"/> records
    /// where an answer came from when not the web UI (the Telegram bot).
    /// </summary>
    public async Task<Clarification> UpdateAsync(
        Clarification clarification, string? question, string? answer, bool? archived, string? origin = null)
    {
        var wasOpen = clarification.IsOpen;
        if (question is not null) clarification.Question = question.Trim();
        if (answer is not null)
        {
            clarification.Answer = answer.Trim();
            clarification.Origin = origin;
        }
        else if (archived == true && origin is not null) clarification.Origin = origin;
        if (archived.HasValue) clarification.IsArchived = archived.Value;
        await _repo.UpdateClarificationAsync(clarification);

        if (wasOpen && !clarification.IsOpen)
            await CompleteRoundIfAnsweredAsync(clarification.BookId);
        return clarification;
    }

    /// <summary>
    /// Archives every active clarification, resetting the round: a run waiting for answers carries on
    /// without them and leaves the round open, so the next planning run asks again.
    /// </summary>
    public async Task ArchiveAllAsync(int bookId)
    {
        await _repo.ArchiveClarificationsAsync(bookId);
        _state.TryResolveClarifications(bookId, roundComplete: false);
    }

    /// <summary>The author marked the round complete: a run waiting for answers carries on.</summary>
    public void ReleaseWaitingRun(int bookId) => _state.TryResolveClarifications(bookId);

    private async Task CompleteRoundIfAnsweredAsync(int bookId)
    {
        if ((await _repo.GetClarificationsAsync(bookId)).Any(c => c.IsOpen)) return;

        if (await _repo.GetByIdAsync(bookId) is { ClarificationsStatus: not PlanningPhaseStatus.Complete } book)
        {
            book.ClarificationsStatus = PlanningPhaseStatus.Complete;
            await _repo.UpdateAsync(book);
        }
        _state.TryResolveClarifications(bookId);
    }
}
