using ABook.Agents;
using ABook.Core.Interfaces;
using ABook.Core.Models;

namespace ABook.Api.Services;

public enum SectionChatResult { Started, NotASectionPause, NotWaiting, Busy, NothingToUndo }

/// <summary>
/// Section-chat operations on a human-assisted review pause, shared by the REST controller and the MCP tools.
/// LLM work uses the paused run's cancellation token, so Stop also ends it, and only one operation per book
/// runs at a time — the pause cannot be completed while one is in progress, so the next step never starts
/// before a change is saved.
/// </summary>
public class SectionChatService(
    IServiceScopeFactory scopeFactory,
    AgentRunStateService runState,
    ILogger<SectionChatService> logger)
{
    /// <summary>
    /// Sends the author's message; the reply is written in the background. <paramref name="completion"/>
    /// finishes when the reply is saved (or failed), for callers that want to wait for it.
    /// </summary>
    public SectionChatResult TrySend(int bookId, AgentMessage pause, string text, out Task completion) =>
        TryRunInBackground(bookId, pause, (agent, target, ct) => agent.ReplyAsync(bookId, pause, target, text, ct), out completion);

    /// <summary>Restores the section to what it was before the chat's latest change, in the background.</summary>
    public async Task<(SectionChatResult Result, Task Completion)> TryUndoAsync(int bookId, AgentMessage pause)
    {
        using (var scope = scopeFactory.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IBookRepository>();
            if (!SectionChatAgent.Changes(await repo.GetMessagesAsync(bookId), pause.Id).Any())
                return (SectionChatResult.NothingToUndo, Task.CompletedTask);
        }
        var result = TryRunInBackground(bookId, pause, (agent, target, ct) => agent.UndoAsync(bookId, pause, target, ct), out var completion);
        return (result, completion);
    }

    /// <summary>Drafts the note of decisions for Complete. Waits for the LLM; empty when there is nothing to keep.</summary>
    public async Task<(SectionChatResult Result, string Summary)> TrySummarizeAsync(int bookId, AgentMessage pause, CancellationToken requestCt)
    {
        var (result, target, runCt) = Claim(bookId, pause);
        if (result != SectionChatResult.Started) return (result, "");
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(runCt, requestCt);
            using var scope = scopeFactory.CreateScope();
            var agent = scope.ServiceProvider.GetRequiredService<SectionChatAgent>();
            return (result, await agent.SummarizeAsync(bookId, pause, target!, linked.Token));
        }
        finally
        {
            runState.EndSectionChat(bookId);
        }
    }

    /// <summary>
    /// Completes the review: saves the author's note as a clarification (when given) and resumes the run.
    /// <paramref name="answer"/> is the pause's answer — rewrite instructions for a chapter's pre-rewrite pause.
    /// </summary>
    public async Task<SectionChatResult> TryCompleteAsync(int bookId, AgentMessage pause, string? note, string? answer)
    {
        var (result, target, _) = Claim(bookId, pause);
        if (result != SectionChatResult.Started) return result;
        // Held until the run has resumed, so no chat message can slip in between.
        try
        {
            using var scope = scopeFactory.CreateScope();
            if (!string.IsNullOrWhiteSpace(note))
                await scope.ServiceProvider.GetRequiredService<SectionChatAgent>().SaveSummaryAsync(bookId, pause, target!, note);
            await scope.ServiceProvider.GetRequiredService<IAgentOrchestrator>()
                .ResumeWithAnswerAsync(pause.Id, answer?.Trim() ?? string.Empty);
        }
        finally
        {
            runState.EndSectionChat(bookId);
        }
        return SectionChatResult.Started;
    }

    /// <summary>The pause of the book's live run, if the run is parked on a review pause.</summary>
    public async Task<AgentMessage?> FindLivePauseAsync(int bookId)
    {
        if (runState.GetPendingMessageId(bookId) is not int id) return null;
        using var scope = scopeFactory.CreateScope();
        var pause = await scope.ServiceProvider.GetRequiredService<IBookRepository>().FindMessageByIdAsync(id);
        return pause is not null && SectionChatTarget.FromPause(pause) is not null ? pause : null;
    }

    /// <summary>Checks the pause is live and claims the book's chat for one operation.</summary>
    private (SectionChatResult Result, SectionChatTarget? Target, CancellationToken RunCt) Claim(int bookId, AgentMessage pause)
    {
        var target = SectionChatTarget.FromPause(pause);
        if (target is null) return (SectionChatResult.NotASectionPause, null, default);
        if (pause.IsResolved || runState.GetPendingMessageId(bookId) != pause.Id
            || !runState.TryGetRunToken(bookId, out var ct))
            return (SectionChatResult.NotWaiting, null, default);
        if (!runState.TryBeginSectionChat(bookId, pause.Id)) return (SectionChatResult.Busy, null, default);
        return (SectionChatResult.Started, target, ct);
    }

    private SectionChatResult TryRunInBackground(
        int bookId, AgentMessage pause, Func<SectionChatAgent, SectionChatTarget, CancellationToken, Task> work, out Task completion)
    {
        var (result, target, ct) = Claim(bookId, pause);
        completion = Task.CompletedTask;
        if (result != SectionChatResult.Started) return result;
        completion = Task.Run(() => RunAsync(bookId, pause, target!, work, ct));
        return result;
    }

    private async Task RunAsync(int bookId, AgentMessage pause, SectionChatTarget target,
        Func<SectionChatAgent, SectionChatTarget, CancellationToken, Task> work, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var notifier = scope.ServiceProvider.GetRequiredService<IBookNotifier>();
        try
        {
            await work(scope.ServiceProvider.GetRequiredService<SectionChatAgent>(), target, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped by the author — the run is being cancelled as well.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Book {BookId}] Section chat failed.", bookId);
        }
        finally
        {
            runState.EndSectionChat(bookId);
            try { await notifier.NotifySectionChatAsync(bookId, pause.Id, busy: false, CancellationToken.None); }
            catch (Exception ex) { logger.LogWarning(ex, "[Book {BookId}] Section chat notification failed.", bookId); }
        }
    }

    public static string ToMessage(SectionChatResult result) => result switch
    {
        SectionChatResult.NotASectionPause => "This question is not a review pause that can be discussed.",
        SectionChatResult.NotWaiting => "The run is no longer waiting on this pause.",
        SectionChatResult.Busy => "The LLM is still working on the previous request.",
        SectionChatResult.NothingToUndo => "The chat has not changed the section yet.",
        _ => "Started.",
    };
}
