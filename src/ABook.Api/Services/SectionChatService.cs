using ABook.Agents;
using ABook.Core.Interfaces;
using ABook.Core.Models;

namespace ABook.Api.Services;

public enum SectionChatSendResult { Started, NotASectionPause, NotWaiting, Busy }

/// <summary>
/// Runs section-chat replies in the background while a human-assisted run is paused. A reply uses the
/// paused run's cancellation token, so Stop also ends a reply in progress, and only one reply per book
/// runs at a time — the pause cannot be answered (Complete) while one is in progress.
/// </summary>
public class SectionChatService(
    IServiceScopeFactory scopeFactory,
    AgentRunStateService runState,
    ILogger<SectionChatService> logger)
{
    public SectionChatSendResult TrySend(int bookId, AgentMessage pause, string text)
    {
        var target = SectionChatTarget.FromPause(pause);
        if (target is null) return SectionChatSendResult.NotASectionPause;
        if (pause.IsResolved || runState.GetPendingMessageId(bookId) != pause.Id
            || !runState.TryGetRunToken(bookId, out var ct))
            return SectionChatSendResult.NotWaiting;
        if (!runState.TryBeginSectionChat(bookId, pause.Id)) return SectionChatSendResult.Busy;

        _ = Task.Run(() => RunAsync(bookId, pause, target, text, ct));
        return SectionChatSendResult.Started;
    }

    private async Task RunAsync(int bookId, AgentMessage pause, SectionChatTarget target, string text, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var notifier = scope.ServiceProvider.GetRequiredService<IBookNotifier>();
        try
        {
            var agent = scope.ServiceProvider.GetRequiredService<SectionChatAgent>();
            await agent.ReplyAsync(bookId, pause, target, text, ct);
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

    public static string ToMessage(SectionChatSendResult result) => result switch
    {
        SectionChatSendResult.NotASectionPause => "This question is not about a section that can be discussed.",
        SectionChatSendResult.NotWaiting => "The run is no longer waiting on this pause.",
        SectionChatSendResult.Busy => "The LLM is still answering the previous message.",
        _ => "Started.",
    };
}
