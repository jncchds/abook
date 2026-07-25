using ABook.Agents;
using ABook.Core.Interfaces;

namespace ABook.Api.Services;

/// <summary>
/// Shared fire-and-forget runner for agent orchestrator calls. Handles terminal-state
/// recovery so a book is never left stuck in "Running" if the orchestrator error path
/// itself throws.
/// </summary>
public class AgentRunnerService(
    IServiceScopeFactory scopeFactory,
    AgentRunStateService runState,
    ILogger<AgentRunnerService> logger)
{
    public async Task RunAsync(int bookId, Func<IAgentOrchestrator, CancellationToken, Task> action, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IAgentOrchestrator>();
        try
        {
            await action(orchestrator, ct);
        }
        catch (OperationCanceledException)
        {
            // Expected when stopped by the user — state already set by orchestrator
        }
        catch (Exception ex)
        {
            // The orchestrator's error handler should have dealt with this. If we reach
            // here, that handler itself threw — force a terminal state so the book is
            // never stuck on "Running" forever.
            logger.LogError(ex,
                "[Book {BookId}] Agent run failed outside orchestrator error handler — forcing terminal state.",
                bookId);
            var current = runState.GetStatus(bookId);
            if (current is { State: "Running" or "WaitingForInput" })
            {
                runState.SetStatus(bookId, new AgentRunStatus(current.Role, "Failed", current.ChapterId));
                runState.ClearStreamBuffers(bookId);
                runState.TryRemoveRunId(bookId, out _);
            }
        }
    }
}
