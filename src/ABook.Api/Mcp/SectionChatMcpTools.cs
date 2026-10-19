using ABook.Agents;
using ABook.Api.Services;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace ABook.Api.Mcp;

/// <summary>MCP tools for the section chat of a human-assisted review pause.</summary>
public class SectionChatMcpTools(
    SectionChatService chat, IBookRepository repo, AgentRunStateService runState, IHttpContextAccessor http)
    : McpToolBase(http)
{
    private const int MaxWaitSeconds = 600;

    /// <summary>The live review pause of an owned book, or an error saying why there is none.</summary>
    private async Task<(AgentMessage Pause, SectionChatTarget Target)> LivePauseAsync(int bookId)
    {
        await EnsureBookOwnershipAsync(bookId, repo);
        var pause = await chat.FindLivePauseAsync(bookId)
            ?? throw new McpException(
                "The book's run is not paused for review. Review pauses happen in human-assisted mode after each " +
                "planning phase, before each chapter is written and before each chapter's creative rewrite; " +
                "use get_agent_status to see whether it is waiting.");
        return (pause, SectionChatTarget.FromPause(pause)!);
    }

    private async Task<object> StateAsync(int bookId, AgentMessage pause, SectionChatTarget target)
    {
        var messages = (await repo.GetMessagesAsync(bookId)).ToList();
        return new
        {
            bookId,
            questionMessageId = pause.Id,
            section = target.Section.ToString(),
            target.ChapterId,
            pauseQuestion = pause.Content,
            busy = runState.IsSectionChatBusy(bookId),
            messages = SectionChatAgent.Conversation(messages, pause.Id).Select(m => new
            {
                m.Id,
                from = m.MessageType == MessageType.ChatUser ? "author" : "llm",
                m.Content,
                m.CreatedAt,
            }),
            changes = SectionChatAgent.Changes(messages, pause.Id).Select(c => new { c.Id, c.ReplyId }),
        };
    }

    [McpServerTool(Name = "get_section_chat")]
    [Description("Get the section chat of the review pause the book's run is waiting on: which section it is about (StoryBible, Characters, PlotThreads, ChapterOutlines, ChapterOutline = one chapter's outline before it is written, Chapter = a chapter's prose before its creative rewrite; chapterId for the last two), whether the LLM is busy, the conversation, and the changes the chat made (each linked to the reply that made it). Read the section itself with the content tools.")]
    public async Task<string> GetSectionChat([Description("The book ID.")] int bookId)
    {
        var (pause, target) = await LivePauseAsync(bookId);
        return JsonSerializer.Serialize(await StateAsync(bookId, pause, target), JsonOptions);
    }

    [McpServerTool(Name = "send_section_chat_message")]
    [Description("Send a message to the LLM about the section under review — a question, a comment, or a requested change. The LLM sees the section as it was when the chat began, the whole conversation and the current version; when a change is needed it rewrites the section, which is saved at once (with history). Waits for the reply up to waitSeconds and returns the chat; if the reply is not ready yet, poll get_section_chat until busy is false. Finish the review with complete_section_review.")]
    public async Task<string> SendSectionChatMessage(
        [Description("The book ID.")] int bookId,
        [Description("The message to the LLM.")] string message,
        [Description("How long to wait for the reply, in seconds (0 = return immediately, max 600). Default 120.")] int waitSeconds = 120)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new McpException("The message is empty.");
        var (pause, target) = await LivePauseAsync(bookId);
        var result = chat.TrySend(bookId, pause, message.Trim(), out var completion);
        if (result != SectionChatResult.Started) throw new McpException(SectionChatService.ToMessage(result));
        return await WaitAndReportAsync(bookId, pause, target, completion, waitSeconds);
    }

    [McpServerTool(Name = "undo_section_chat_change")]
    [Description("Restore the section under review to what it was before the chat's latest change (this also replaces any edit made by hand since then). The undo is itself recorded as a change, so calling it again redoes the change. Returns the chat.")]
    public async Task<string> UndoSectionChatChange(
        [Description("The book ID.")] int bookId,
        [Description("How long to wait, in seconds (max 600). Default 60.")] int waitSeconds = 60)
    {
        var (pause, target) = await LivePauseAsync(bookId);
        var (result, completion) = await chat.TryUndoAsync(bookId, pause);
        if (result != SectionChatResult.Started) throw new McpException(SectionChatService.ToMessage(result));
        return await WaitAndReportAsync(bookId, pause, target, completion, waitSeconds);
    }

    [McpServerTool(Name = "summarize_section_chat")]
    [Description("Ask the LLM to draft a short note of the decisions and preferences from the section chat that should guide the rest of the book. Returns the draft (empty when there is nothing worth keeping). Edit it as needed and pass it as 'note' to complete_section_review, which saves it as a clarification that every later prompt includes.")]
    public async Task<string> SummarizeSectionChat([Description("The book ID.")] int bookId)
    {
        var (pause, _) = await LivePauseAsync(bookId);
        var (result, summary) = await chat.TrySummarizeAsync(bookId, pause, CancellationToken.None);
        if (result != SectionChatResult.Started) throw new McpException(SectionChatService.ToMessage(result));
        return JsonSerializer.Serialize(new { bookId, summary }, JsonOptions);
    }

    [McpServerTool(Name = "complete_section_review")]
    [Description("Finish the review pause and let the run continue with the section as it is now. Optionally saves 'note' (e.g. an edited summarize_section_chat draft) as a clarification. 'rewriteInstructions' only applies to the pause before a chapter's creative rewrite (section Chapter): they are passed to the rewrite.")]
    public async Task<string> CompleteSectionReview(
        [Description("The book ID.")] int bookId,
        [Description("Decisions to keep for the rest of the book; saved as a clarification. Omit to save nothing.")] string? note = null,
        [Description("Instructions for the chapter's creative rewrite (section Chapter only).")] string? rewriteInstructions = null)
    {
        var (pause, target) = await LivePauseAsync(bookId);
        var answer = target.Section == ChatSection.Chapter ? rewriteInstructions : null;
        var result = await chat.TryCompleteAsync(bookId, pause, note, answer);
        if (result != SectionChatResult.Started) throw new McpException(SectionChatService.ToMessage(result));
        return JsonSerializer.Serialize(new { completed = true, bookId, noteSaved = !string.IsNullOrWhiteSpace(note) }, JsonOptions);
    }

    private async Task<string> WaitAndReportAsync(
        int bookId, AgentMessage pause, SectionChatTarget target, Task completion, int waitSeconds)
    {
        var wait = TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 0, MaxWaitSeconds));
        if (wait > TimeSpan.Zero) await Task.WhenAny(completion, Task.Delay(wait));
        return JsonSerializer.Serialize(await StateAsync(bookId, pause, target), JsonOptions);
    }
}
