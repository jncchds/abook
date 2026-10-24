using ABook.Agents;
using ABook.Api.Services;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ABook.Api.Controllers;

/// <summary>
/// The chat an Interactive book is written in: messages, the changes the LLM proposes, applying, rejecting and
/// undoing them, and the premise/Story Bible lock. Replies and applied changes run in the background; the UI
/// re-reads the chat on the BookChatUpdated SignalR event.
/// </summary>
[ApiController]
[Route("api/books/{bookId:int}/book-chat")]
[Authorize]
public class BookChatController(IBookRepository repo, BookChatService chat, AgentRunStateService runState) : ControllerBase
{
    /// <summary>The newest <paramref name="limit"/> messages before <paramref name="before"/> (oldest first), with their proposals.</summary>
    [HttpGet]
    public async Task<IActionResult> Get(int bookId, [FromQuery] int? before = null, [FromQuery] int limit = 50)
    {
        var denied = await this.RequireBookOwnershipAsync(bookId, repo);
        if (denied is not null) return denied;
        var book = await repo.GetByIdAsync(bookId);
        if (book is null) return NotFound();

        // The web UI shows messages hidden by a rewind too (collapsed, read-only).
        var messages = await repo.GetChatMessagesAsync(bookId, before, Math.Clamp(limit, 1, 200), includeHidden: true);
        var status = runState.GetStatus(bookId);
        return Ok(new
        {
            Mode = book.Mode,
            book.StoryBibleLockedAt,
            Busy = status is { IsActive: true },
            BusyRole = status is { IsActive: true } ? status.Role.ToString() : null,
            HasMore = messages.Count > 0 && (await repo.GetChatMessagesAsync(bookId, messages[0].Id, 1, includeHidden: true)).Count > 0,
            Messages = messages,
        });
    }

    [HttpPost]
    public async Task<IActionResult> Send(int bookId, [FromBody] BookChatSendRequest req) =>
        await RunAsync(bookId, () => chat.SendAsync(bookId, req.Message ?? ""));

    [HttpPost("retry/{messageId:int}")]
    public async Task<IActionResult> Retry(int bookId, int messageId) =>
        await RunAsync(bookId, () => chat.RetryAsync(bookId, messageId));

    [HttpPost("kickoff")]
    public async Task<IActionResult> Kickoff(int bookId) =>
        await RunAsync(bookId, () => chat.KickoffAsync(bookId));

    [HttpPost("proposals/apply")]
    public async Task<IActionResult> Apply(int bookId, [FromBody] BookChatProposalsRequest req) =>
        await RunAsync(bookId, () => chat.ApplyAsync(bookId, req.Ids ?? [], req.Force));

    [HttpPost("proposals/reject")]
    public async Task<IActionResult> Reject(int bookId, [FromBody] BookChatProposalsRequest req) =>
        await RunAsync(bookId, () => chat.RejectAsync(bookId, req.Ids ?? []));

    [HttpPost("proposals/{proposalId:int}/undo")]
    public async Task<IActionResult> Undo(int bookId, int proposalId, [FromBody] BookChatUndoRequest? req) =>
        await RunAsync(bookId, () => chat.UndoAsync(bookId, proposalId, req?.Force ?? false));

    [HttpPost("lock-bible")]
    public async Task<IActionResult> LockBible(int bookId) =>
        await RunAsync(bookId, () => chat.LockBibleAsync(bookId));

    /// <summary>Author only — the LLM never proposes it and MCP does not offer it.</summary>
    [HttpPost("unlock-bible")]
    public async Task<IActionResult> UnlockBible(int bookId) =>
        await RunAsync(bookId, () => chat.UnlockBibleAsync(bookId));

    private async Task<IActionResult> RunAsync(int bookId, Func<Task<BookChatOutcome>> operation)
    {
        var denied = await this.RequireBookOwnershipAsync(bookId, repo);
        if (denied is not null) return denied;
        var outcome = await operation();
        return outcome.Result switch
        {
            BookChatResult.Started => Accepted(new { outcome.MessageId }),
            BookChatResult.Done => Ok(new { outcome.Message }),
            BookChatResult.NotFound => NotFound(new { outcome.Message }),
            BookChatResult.Invalid or BookChatResult.NotInteractive => BadRequest(new { outcome.Message }),
            _ => Conflict(new { outcome.Message, outcome.Stale, Result = outcome.Result.ToString() }),
        };
    }
}

public record BookChatSendRequest(string? Message);
public record BookChatProposalsRequest(IReadOnlyList<int>? Ids, bool Force = false);
public record BookChatUndoRequest(bool Force = false);
