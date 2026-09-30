using ABook.Agents;
using ABook.Core.Interfaces;
using ABook.Core.Models;

namespace ABook.Api.Services;

/// <summary>
/// The single entry point for starting agent runs (REST controllers and MCP tools alike).
/// <see cref="TryStart"/> claims the book synchronously, then runs the orchestrator call in the
/// background with terminal-state recovery, so a book is never left stuck in "Running" even if
/// the orchestrator's own error path throws.
/// </summary>
public class AgentRunnerService(
    IServiceScopeFactory scopeFactory,
    AgentRunStateService runState,
    ILogger<AgentRunnerService> logger)
{
    /// <summary>
    /// Claims the book and a concurrency slot and, on success, starts <paramref name="action"/> in the
    /// background. <paramref name="role"/> and <paramref name="chapterId"/> are the run's initial status.
    /// </summary>
    public RunStartResult TryStart(
        int bookId, AgentRole role, int? chapterId, Func<IAgentOrchestrator, CancellationToken, Task> action)
    {
        var result = runState.TryStartRun(bookId, role, chapterId, out var ct);
        if (result == RunStartResult.Success)
            _ = Task.Run(() => RunAsync(bookId, action, ct));
        return result;
    }

    private async Task RunAsync(int bookId, Func<IAgentOrchestrator, CancellationToken, Task> action, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<IAgentOrchestrator>();
            await action(orchestrator, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected when stopped by the user — state already set by orchestrator
        }
        catch (Exception ex)
        {
            // The orchestrator's error handler normally records the failure and rethrows; the log
            // line there has the details. Only act if it failed to leave a terminal state behind.
            var current = runState.GetStatus(bookId);
            if (current is { IsActive: true })
            {
                logger.LogError(ex,
                    "[Book {BookId}] Agent run failed outside orchestrator error handler — forcing terminal state.",
                    bookId);
                runState.SetStatus(bookId, current with { State = RunStates.Failed });
                runState.ClearStreamBuffers(bookId);
                await runState.PersistRunFinishedAsync(bookId, AgentRunPersistStatus.Failed);
            }
        }
        finally
        {
            runState.ReleaseRunCts(bookId, ct);
        }
    }
}

/// <summary>Maps a failed <see cref="RunStartResult"/> to the message shown to the caller.</summary>
public static class RunStartResultExtensions
{
    public static string ToMessage(this RunStartResult result) => result switch
    {
        RunStartResult.BookBusy => "An agent run is already in progress for this book. Stop it first or wait for it to complete.",
        RunStartResult.AtCapacity => "Server is at maximum concurrent agent capacity. Please try again when a run completes.",
        _ => "Started.",
    };
}
