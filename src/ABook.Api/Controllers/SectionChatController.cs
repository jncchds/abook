using ABook.Agents;
using ABook.Api.Services;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ABook.Api.Controllers;

/// <summary>
/// The chat with the LLM about the section a human-assisted run is paused on for review: messages,
/// undo, the summary drafted at Complete, and Complete itself.
/// </summary>
[ApiController]
[Route("api/books/{bookId:int}/section-chat/{questionMessageId:int}")]
[Authorize]
public class SectionChatController(
    IBookRepository repo, AgentRunStateService runState, SectionChatService chat, SectionChatAgent agent) : ControllerBase
{
    /// <summary>The pause's section, whether the LLM is busy, the conversation and the changes it made.</summary>
    [HttpGet]
    public async Task<IActionResult> Get(int bookId, int questionMessageId)
    {
        var (error, pause) = await LoadPauseAsync(bookId, questionMessageId);
        if (error is not null) return error;
        var target = SectionChatTarget.FromPause(pause!);
        if (target is null) return BadRequest(new { message = SectionChatService.ToMessage(SectionChatResult.NotASectionPause) });

        var messages = (await repo.GetMessagesAsync(bookId)).ToList();
        var live = !pause!.IsResolved && runState.GetPendingMessageId(bookId) == questionMessageId;
        return Ok(new
        {
            Section = target.Section.ToString(),
            target.ChapterId,
            Busy = runState.IsSectionChatBusy(bookId),
            Live = live,
            Messages = SectionChatAgent.Conversation(messages, questionMessageId),
            Changes = SectionChatAgent.Changes(messages, questionMessageId),
            // False when the author edited the section by hand after the chat's latest change: Undo would discard that.
            UndoKeepsEdits = !live || await agent.MatchesLatestChangeAsync(bookId, questionMessageId, target),
        });
    }

    /// <summary>Sends the author's message; the reply arrives over SignalR (SectionChatUpdated, then the chat is re-read).</summary>
    [HttpPost]
    public async Task<IActionResult> Send(int bookId, int questionMessageId, [FromBody] SectionChatRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Message)) return BadRequest(new { message = "The message is empty." });
        var (error, pause) = await LoadPauseAsync(bookId, questionMessageId);
        if (error is not null) return error;
        return ToResponse(chat.TrySend(bookId, pause!, req.Message.Trim(), out _));
    }

    /// <summary>Restores the section to what it was before the chat's latest change (in the background, like a reply).</summary>
    [HttpPost("undo")]
    public async Task<IActionResult> Undo(int bookId, int questionMessageId)
    {
        var (error, pause) = await LoadPauseAsync(bookId, questionMessageId);
        if (error is not null) return error;
        return ToResponse((await chat.TryUndoAsync(bookId, pause!)).Result);
    }

    /// <summary>Drafts the note of decisions shown when the author completes the review. Waits for the LLM.</summary>
    [HttpPost("summary")]
    public async Task<IActionResult> Summary(int bookId, int questionMessageId, CancellationToken ct)
    {
        var (error, pause) = await LoadPauseAsync(bookId, questionMessageId);
        if (error is not null) return error;
        var (result, summary) = await chat.TrySummarizeAsync(bookId, pause!, ct);
        return result == SectionChatResult.Started ? Ok(new { summary }) : ToResponse(result);
    }

    /// <summary>Saves the (edited) note as a clarification when one is given, then resumes the run.</summary>
    [HttpPost("complete")]
    public async Task<IActionResult> Complete(int bookId, int questionMessageId, [FromBody] SectionChatCompleteRequest req)
    {
        var (error, pause) = await LoadPauseAsync(bookId, questionMessageId);
        if (error is not null) return error;
        var result = await chat.TryCompleteAsync(bookId, pause!, req.Note, req.Answer);
        return result == SectionChatResult.Started ? Ok() : ToResponse(result);
    }

    private IActionResult ToResponse(SectionChatResult result) => result switch
    {
        SectionChatResult.Started => Accepted(),
        SectionChatResult.NotASectionPause => BadRequest(new { message = SectionChatService.ToMessage(result) }),
        _ => Conflict(new { message = SectionChatService.ToMessage(result) }),
    };

    private async Task<(IActionResult? Error, AgentMessage? Pause)> LoadPauseAsync(int bookId, int questionMessageId)
    {
        var denied = await this.RequireBookOwnershipAsync(bookId, repo);
        if (denied is not null) return (denied, null);
        var pause = await repo.FindMessageByIdAsync(questionMessageId);
        if (pause is null || pause.BookId != bookId || pause.IsDeleted) return (NotFound(), null);
        return (null, pause);
    }
}

public record SectionChatRequest(string Message);

/// <param name="Note">The decisions to keep, saved as a clarification; null or blank saves nothing.</param>
/// <param name="Answer">The pause's answer — optional rewrite instructions for a chapter's pre-rewrite pause.</param>
public record SectionChatCompleteRequest(string? Note, string? Answer);
