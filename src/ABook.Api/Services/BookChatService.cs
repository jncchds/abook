using ABook.Agents;
using ABook.Core.Interfaces;
using ABook.Core.Models;

namespace ABook.Api.Services;

public enum BookChatResult { Started, Done, NotInteractive, Busy, AtCapacity, Invalid, NotFound, Stale, Blocked }

/// <param name="Stale">Proposals whose target changed since (apply) or since they were applied (undo) — confirm with force.</param>
public sealed record BookChatOutcome(
    BookChatResult Result, string? Message = null, int? MessageId = null, IReadOnlyList<StaleProposal>? Stale = null);

public sealed record StaleProposal(int Id, string Label);

/// <summary>
/// Book-chat operations of an Interactive book, shared by the REST controller and the MCP tools. Every
/// operation that calls the LLM or changes the book (reply, apply, undo) is an agent run started through
/// <see cref="AgentRunnerService"/>, so the book does one thing at a time and Stop ends it.
/// </summary>
public class BookChatService(
    IServiceScopeFactory scopeFactory,
    AgentRunnerService runner,
    AgentRunStateService runState)
{
    /// <summary>Saves the author's message and starts the reply in the background.</summary>
    public async Task<BookChatOutcome> SendAsync(int bookId, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new(BookChatResult.Invalid, "The message is empty.");
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IBookRepository>();
        if (await RequireInteractiveAsync(repo, bookId) is { } refused) return refused;
        if (IsBusy(bookId)) return Busy();

        var message = await repo.AddChatMessageAsync(new BookChatMessage
        {
            BookId = bookId, Role = BookChatRole.Author, Content = text.Trim(), Status = BookChatMessageStatus.Pending,
        });
        return await StartReplyAsync(repo, scope, bookId, message);
    }

    /// <summary>Answers again an author message whose reply failed or was stopped.</summary>
    public async Task<BookChatOutcome> RetryAsync(int bookId, int messageId)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IBookRepository>();
        if (await RequireInteractiveAsync(repo, bookId) is { } refused) return refused;
        var message = await repo.GetChatMessageAsync(bookId, messageId);
        if (message is not { Role: BookChatRole.Author, Status: BookChatMessageStatus.Failed })
            return new(BookChatResult.NotFound, "Only a message whose reply failed can be retried.");
        if (IsBusy(bookId)) return Busy();
        message.Status = BookChatMessageStatus.Pending;
        await repo.UpdateChatMessageAsync(message);
        return await StartReplyAsync(repo, scope, bookId, message);
    }

    private async Task<BookChatOutcome> StartReplyAsync(IBookRepository repo, IServiceScope scope, int bookId, BookChatMessage message)
    {
        var notifier = scope.ServiceProvider.GetRequiredService<IBookNotifier>();
        var result = runner.TryStart(bookId, AgentRole.BookChat, null, (o, c) => o.RunBookChatTurnAsync(bookId, message.Id, c));
        if (result != RunStartResult.Success)
        {
            message.Status = BookChatMessageStatus.Failed;
            await repo.UpdateChatMessageAsync(message);
        }
        await notifier.NotifyBookChatAsync(bookId, message.Id);
        return result == RunStartResult.Success
            ? new(BookChatResult.Started, MessageId: message.Id)
            : FromRunStart(result, message.Id);
    }

    /// <summary>
    /// The opening reply of a new Interactive book: a Story Bible proposal and questions. Only while nobody has
    /// written in the chat yet — events (such as a failed first attempt) do not count, so it can be retried.
    /// </summary>
    public async Task<BookChatOutcome> KickoffAsync(int bookId)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IBookRepository>();
        if (await RequireInteractiveAsync(repo, bookId) is { } refused) return refused;
        if ((await repo.GetChatMessagesAsync(bookId)).Any(m => m.Role != BookChatRole.Event))
            return new(BookChatResult.Invalid, "The chat has already started.");
        if (IsBusy(bookId)) return Busy();
        return Start(bookId, (o, c) => o.RunBookChatTurnAsync(bookId, null, c));
    }

    /// <summary>
    /// Applies proposals in the background. Without <paramref name="force"/>, refuses (Stale) when a target changed
    /// since it was proposed, so the author can confirm overwriting that change.
    /// </summary>
    public async Task<BookChatOutcome> ApplyAsync(int bookId, IReadOnlyList<int> ids, bool force)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IBookRepository>();
        if (await RequireInteractiveAsync(repo, bookId) is { } refused) return refused;
        var proposals = (await repo.GetProposalsAsync(bookId, ids))
            .Where(p => p.Status is ChatProposalStatus.Pending or ChatProposalStatus.Failed)
            .ToList();
        if (proposals.Count == 0) return new(BookChatResult.NotFound, "None of these changes can be applied (any more).");
        if (IsBusy(bookId)) return Busy();

        if (!force)
        {
            var stale = await scope.ServiceProvider.GetRequiredService<ProposalExecutor>().FindStaleAsync(bookId, proposals);
            if (stale.Count > 0)
                return new(BookChatResult.Stale,
                    "Changed since it was proposed — applying it overwrites that change.",
                    Stale: stale.Select(p => new StaleProposal(p.Id, p.Label)).ToList());
        }
        var applyIds = proposals.Select(p => p.Id).ToList();
        return Start(bookId, (o, c) => o.ApplyProposalsAsync(bookId, applyIds, c));
    }

    /// <summary>Rejects pending proposals. Nothing in the book changes, so this runs right away.</summary>
    public async Task<BookChatOutcome> RejectAsync(int bookId, IReadOnlyList<int> ids)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IBookRepository>();
        if (await RequireInteractiveAsync(repo, bookId) is { } refused) return refused;
        var messages = new HashSet<int>();
        foreach (var p in await repo.GetProposalsAsync(bookId, ids))
            if (p.Status is ChatProposalStatus.Pending or ChatProposalStatus.Failed)
            {
                p.Status = ChatProposalStatus.Rejected;
                p.ResultNote = "Rejected by the author.";
                await repo.UpdateProposalAsync(p);
                messages.Add(p.MessageId);
            }
        if (messages.Count == 0) return new(BookChatResult.NotFound, "None of these changes are pending.");
        await scope.ServiceProvider.GetRequiredService<IBookNotifier>()
            .NotifyBookChatAsync(bookId, messages.Count == 1 ? messages.First() : null);
        return new(BookChatResult.Done);
    }

    /// <summary>
    /// Undoes an applied proposal in the background. Without <paramref name="force"/>, refuses (Stale) when its target
    /// was edited after it was applied, since undoing discards that edit.
    /// </summary>
    public async Task<BookChatOutcome> UndoAsync(int bookId, int proposalId, bool force)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IBookRepository>();
        if (await RequireInteractiveAsync(repo, bookId) is { } refused) return refused;
        var proposal = await repo.GetProposalAsync(bookId, proposalId);
        if (proposal is null) return new(BookChatResult.NotFound, "Change not found.");
        if (IsBusy(bookId)) return Busy();

        var executor = scope.ServiceProvider.GetRequiredService<ProposalExecutor>();
        if (await executor.UndoBlockerAsync(bookId, proposal) is { } blocker) return new(BookChatResult.Blocked, blocker);
        if (!force && await executor.IsUndoStaleAsync(bookId, proposal))
            return new(BookChatResult.Stale, "It was edited after this change — undoing discards that edit.",
                Stale: [new StaleProposal(proposal.Id, proposal.Label)]);
        return Start(bookId, (o, c) => o.UndoProposalAsync(bookId, proposalId, c));
    }

    /// <summary>Locks the premise and Story Bible: the book starts its writing stage.</summary>
    public Task<BookChatOutcome> LockBibleAsync(int bookId) => SetBibleLockAsync(bookId, locked: true);

    /// <summary>Author only (never offered to the LLM or over MCP): makes the premise and Story Bible editable again.</summary>
    public Task<BookChatOutcome> UnlockBibleAsync(int bookId) => SetBibleLockAsync(bookId, locked: false);

    private async Task<BookChatOutcome> SetBibleLockAsync(int bookId, bool locked)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IBookRepository>();
        if (await RequireInteractiveAsync(repo, bookId) is { } refused) return refused;
        if (IsBusy(bookId)) return Busy();
        var content = scope.ServiceProvider.GetRequiredService<BookContentService>();
        var changed = locked ? await content.LockStoryBibleAsync(bookId) : await content.UnlockStoryBibleAsync(bookId);
        if (!changed) return new(BookChatResult.Done, locked ? "Already locked." : "Not locked.");
        await scope.ServiceProvider.GetRequiredService<BookChatAgent>().AddEventAsync(bookId, locked
            ? "📌 The premise and Story Bible are now locked — writing has started."
            : "🔓 The author unlocked the premise and Story Bible.");
        return new(BookChatResult.Done);
    }

    /// <summary>Waits until the book's run has finished, or the timeout passes. True when the book is idle.</summary>
    public async Task<bool> WaitUntilIdleAsync(int bookId, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (IsBusy(bookId))
        {
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(500, ct);
        }
        return true;
    }

    public bool IsBusy(int bookId) => runState.GetStatus(bookId) is { IsActive: true };

    private BookChatOutcome Start(int bookId, Func<IAgentOrchestrator, CancellationToken, Task> action)
    {
        var result = runner.TryStart(bookId, AgentRole.BookChat, null, action);
        return result == RunStartResult.Success ? new(BookChatResult.Started) : FromRunStart(result, null);
    }

    private static BookChatOutcome FromRunStart(RunStartResult result, int? messageId) => result switch
    {
        RunStartResult.AtCapacity => new(BookChatResult.AtCapacity, result.ToMessage(), messageId),
        _ => new(BookChatResult.Busy, result.ToMessage(), messageId),
    };

    private static BookChatOutcome Busy() =>
        new(BookChatResult.Busy, "The book is busy (a reply or a change is in progress). Wait for it to finish or stop it.");

    private static async Task<BookChatOutcome?> RequireInteractiveAsync(IBookRepository repo, int bookId)
    {
        var book = await repo.GetByIdAsync(bookId);
        if (book is null) return new(BookChatResult.NotFound, "Book not found.");
        return book.Mode == BookMode.Interactive
            ? null
            : new(BookChatResult.NotInteractive, "The book chat is only for Interactive books. Switch the book's mode in Book Settings.");
    }
}
