using ABook.Api.Services;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace ABook.Api.Mcp;

/// <summary>
/// MCP tools for the book chat of Interactive books: talk to the co-author LLM, then apply, reject or undo the
/// changes it proposes. Unlocking the premise and Story Bible is deliberately not offered here — only the author
/// can do that, in Book Settings.
/// </summary>
public class BookChatMcpTools(BookChatService chat, IBookRepository repo, IServiceScopeFactory scopeFactory, IHttpContextAccessor http)
    : McpToolBase(http)
{
    private const string WaitHelp = "Seconds to wait for the work to finish (0 returns at once; max 600).";

    [McpServerTool(Name = "get_book_chat", ReadOnly = true)]
    [Description("Read the book chat of an Interactive book: the latest messages (oldest first) and, on each reply, the conflicts the LLM flagged and the changes it proposed — with their id, type, label, status (Pending, Applying, Applied, Rejected, Superseded, Failed, Undone), previewBefore/previewAfter and resultNote. Also whether the book is busy and whether the premise and Story Bible are locked.")]
    public async Task<string> GetBookChat(
        [Description("The book ID.")] int bookId,
        [Description("How many of the latest messages to return (default 20).")] int limit = 20)
    {
        var book = await GetOwnedBookAsync(bookId, repo);
        return await SnapshotAsync(book, limit);
    }

    [McpServerTool(Name = "send_book_chat_message")]
    [Description("Send a message to the co-author LLM of an Interactive book — e.g. introduce a character, add a plot thread, add or write a chapter, change something. It replies and proposes changes, which do nothing until applied with apply_book_chat_proposals. Waits for the reply and returns the latest messages.")]
    public async Task<string> SendBookChatMessage(
        [Description("The book ID.")] int bookId,
        [Description("The message.")] string message,
        [Description(WaitHelp)] int waitSeconds = 180)
    {
        var book = await GetOwnedBookAsync(bookId, repo);
        Throw(await chat.SendAsync(bookId, message));
        return await WaitAndSnapshotAsync(book, waitSeconds);
    }

    [McpServerTool(Name = "start_book_chat")]
    [Description("Interactive books with an empty chat: ask the LLM for its opening reply — a Story Bible proposal and questions about the premise.")]
    public async Task<string> StartBookChat(
        [Description("The book ID.")] int bookId,
        [Description(WaitHelp)] int waitSeconds = 180)
    {
        var book = await GetOwnedBookAsync(bookId, repo);
        Throw(await chat.KickoffAsync(bookId));
        return await WaitAndSnapshotAsync(book, waitSeconds);
    }

    [McpServerTool(Name = "apply_book_chat_proposals")]
    [Description("Apply proposed changes (by proposal id). Quick changes run first, then chapter writing/revising; a written chapter is then summarized and checked, and follow-up fixes are posted as new proposals. Applying an alternative (option A/B) rejects the other alternatives of that reply. Writing the first chapter locks the premise and Story Bible. Steps (check_outline, check_chapter, summarize_chapter) run last, post their findings as new proposals, and can be applied again to re-run them. If a target changed since it was proposed, this fails with the stale ids unless force is true.")]
    public async Task<string> ApplyBookChatProposals(
        [Description("The book ID.")] int bookId,
        [Description("The proposal ids to apply.")] int[] proposalIds,
        [Description("Apply even if a target changed since it was proposed (overwrites that change).")] bool force = false,
        [Description(WaitHelp)] int waitSeconds = 300)
    {
        var book = await GetOwnedBookAsync(bookId, repo);
        Throw(await chat.ApplyAsync(bookId, proposalIds, force));
        return await WaitAndSnapshotAsync(book, waitSeconds);
    }

    [McpServerTool(Name = "reject_book_chat_proposals")]
    [Description("Reject proposed changes (by proposal id). Nothing in the book changes.")]
    public async Task<string> RejectBookChatProposals(
        [Description("The book ID.")] int bookId,
        [Description("The proposal ids to reject.")] int[] proposalIds)
    {
        var book = await GetOwnedBookAsync(bookId, repo);
        Throw(await chat.RejectAsync(bookId, proposalIds));
        return await SnapshotAsync(book, 10);
    }

    [McpServerTool(Name = "undo_book_chat_proposal")]
    [Description("Undo an applied change (by proposal id), restoring what it replaced. A later change to the same thing must be undone first. If the target was edited after the change, this fails unless force is true (the edit is discarded).")]
    public async Task<string> UndoBookChatProposal(
        [Description("The book ID.")] int bookId,
        [Description("The applied proposal's id.")] int proposalId,
        [Description("Undo even if the target was edited after the change.")] bool force = false,
        [Description(WaitHelp)] int waitSeconds = 120)
    {
        var book = await GetOwnedBookAsync(bookId, repo);
        Throw(await chat.UndoAsync(bookId, proposalId, force));
        return await WaitAndSnapshotAsync(book, waitSeconds);
    }

    [McpServerTool(Name = "lock_story_bible")]
    [Description("Interactive books: lock the premise and Story Bible and start the writing stage. From then on the chat cannot change them; only the author can unlock them, in Book Settings.")]
    public async Task<string> LockStoryBible([Description("The book ID.")] int bookId)
    {
        var book = await GetOwnedBookAsync(bookId, repo);
        Throw(await chat.LockBibleAsync(bookId));
        return await SnapshotAsync(book, 5);
    }

    private static void Throw(BookChatOutcome outcome)
    {
        if (outcome.Result is BookChatResult.Started or BookChatResult.Done) return;
        var stale = outcome.Stale is { Count: > 0 } s ? $" Stale: {string.Join(", ", s.Select(p => $"{p.Id} \"{p.Label}\""))}." : "";
        throw new McpException($"{outcome.Message}{stale}");
    }

    private async Task<string> WaitAndSnapshotAsync(Book book, int waitSeconds)
    {
        var finished = await chat.WaitUntilIdleAsync(book.Id, TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 0, 600)),
            Http.HttpContext?.RequestAborted ?? CancellationToken.None);
        return await SnapshotAsync(book, 10, finished ? null : "Still working — call get_book_chat later.");
    }

    private async Task<string> SnapshotAsync(Book book, int limit, string? note = null)
    {
        // A fresh scope: the work ran in its own scope, and this request's context still caches what it read before.
        using var scope = scopeFactory.CreateScope();
        var freshRepo = scope.ServiceProvider.GetRequiredService<IBookRepository>();
        var fresh = await freshRepo.GetByIdAsync(book.Id) ?? book;
        var messages = await freshRepo.GetChatMessagesAsync(book.Id, limit: Math.Clamp(limit, 1, 100));
        return JsonSerializer.Serialize(new
        {
            bookId = book.Id,
            mode = fresh.Mode,
            storyBibleLocked = fresh.StoryBibleLockedAt is not null,
            busy = chat.IsBusy(book.Id),
            note,
            messages = messages.Select(m => new
            {
                m.Id, m.Role, m.Status, m.Content, m.CreatedAt,
                conflicts = JsonSerializer.Deserialize<JsonElement>(m.ConflictsJson),
                proposals = m.Proposals.Select(p => new
                {
                    p.Id, p.Type, p.Label, p.Status, p.OptionGroup, p.DependsOnProposalId,
                    p.PreviewBefore, p.PreviewAfter, p.ResultNote,
                }),
            }),
        }, JsonOptions);
    }
}
