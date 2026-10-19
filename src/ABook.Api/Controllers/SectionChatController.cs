using ABook.Agents;
using ABook.Api.Services;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ABook.Api.Controllers;

/// <summary>
/// The chat with the LLM about the section a human-assisted run is paused on. Messages are sent here;
/// the pause itself is completed through <c>POST messages/answer</c> as before.
/// </summary>
[ApiController]
[Route("api/books/{bookId:int}/section-chat/{questionMessageId:int}")]
[Authorize]
public class SectionChatController(
    IBookRepository repo, AgentRunStateService runState, SectionChatService chat) : ControllerBase
{
    /// <summary>The pause's section, whether a reply is in progress, and the conversation so far.</summary>
    [HttpGet]
    public async Task<IActionResult> Get(int bookId, int questionMessageId)
    {
        var (error, pause) = await LoadPauseAsync(bookId, questionMessageId);
        if (error is not null) return error;
        var target = SectionChatTarget.FromPause(pause!);
        if (target is null) return BadRequest(new { message = SectionChatService.ToMessage(SectionChatSendResult.NotASectionPause) });

        var messages = SectionChatAgent.Conversation(await repo.GetMessagesAsync(bookId), questionMessageId);
        return Ok(new
        {
            Section = target.Section.ToString(),
            target.ChapterId,
            Busy = runState.IsSectionChatBusy(bookId),
            Live = !pause!.IsResolved && runState.GetPendingMessageId(bookId) == questionMessageId,
            Messages = messages,
        });
    }

    /// <summary>Sends the author's message; the reply arrives over SignalR (SectionChatUpdated, then the chat is re-read).</summary>
    [HttpPost]
    public async Task<IActionResult> Send(int bookId, int questionMessageId, [FromBody] SectionChatRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Message)) return BadRequest(new { message = "The message is empty." });
        var (error, pause) = await LoadPauseAsync(bookId, questionMessageId);
        if (error is not null) return error;

        var result = chat.TrySend(bookId, pause!, req.Message.Trim());
        return result switch
        {
            SectionChatSendResult.Started => Accepted(),
            SectionChatSendResult.NotASectionPause => BadRequest(new { message = SectionChatService.ToMessage(result) }),
            _ => Conflict(new { message = SectionChatService.ToMessage(result) }),
        };
    }

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
