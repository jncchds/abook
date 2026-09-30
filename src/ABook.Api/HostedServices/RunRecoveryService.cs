using ABook.Agents;
using ABook.Core.Interfaces;
using ABook.Core.Models;

namespace ABook.Api.HostedServices;

/// <summary>
/// Background service that runs once at startup and closes out agent runs left in a non-terminal
/// state (Running or WaitingForInput) by a previous process.
///
/// Neither kind can be resumed in place: the agent's call stack — including one paused on a
/// question — died with the process, so an answer would have nothing to unblock. Every such run is
/// marked Orphaned, its unanswered question (if any) is resolved so the UI stops offering it, and a
/// chat note tells the author to use Continue. Continue/Continue Planning pick up from the persisted
/// chapter and planning-phase statuses, so only the interrupted step is redone.
/// </summary>
public class RunRecoveryService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AgentRunStateService _runState;
    private readonly ILogger<RunRecoveryService> _logger;

    public RunRecoveryService(
        IServiceScopeFactory scopeFactory,
        AgentRunStateService runState,
        ILogger<RunRecoveryService> logger)
    {
        _scopeFactory = scopeFactory;
        _runState = runState;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait briefly for the app to finish starting (migrations, DI warm-up etc.)
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        try
        {
            await RecoverRunsAsync(stoppingToken);
        }
        catch (OperationCanceledException) { /* shutdown before recovery finished */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RunRecoveryService failed during startup recovery.");
        }
    }

    private async Task RecoverRunsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IBookRepository>();
        var notifier = scope.ServiceProvider.GetRequiredService<IBookNotifier>();

        var waiting = (await repo.GetRunsByStatusAsync(AgentRunPersistStatus.WaitingForInput)).ToList();
        var running = (await repo.GetRunsByStatusAsync(AgentRunPersistStatus.Running)).ToList();

        foreach (var run in waiting.Concat(running))
        {
            ct.ThrowIfCancellationRequested();
            var wasWaiting = run.Status == AgentRunPersistStatus.WaitingForInput;
            _logger.LogWarning("[Book {BookId}] Found interrupted {Status} run {RunId} ({RunType}) — marking Orphaned.",
                run.BookId, run.Status, run.Id, run.RunType);

            try
            {
                run.Status = AgentRunPersistStatus.Orphaned;
                var pendingMessageId = run.PendingMessageId;
                run.PendingMessageId = null;
                await repo.UpdateRunAsync(run);

                // The question can no longer be answered — resolve it so the UI stops showing it as open.
                if (pendingMessageId is int messageId && await repo.FindMessageByIdAsync(messageId) is { IsResolved: false } question)
                {
                    question.IsResolved = true;
                    await repo.UpdateMessageAsync(question);
                }

                var reason = wasWaiting
                    ? "The server restarted while the agent was waiting for your answer, so that question can no longer be answered."
                    : "Agent run was interrupted by a server restart.";
                await repo.AddMessageAsync(new AgentMessage
                {
                    BookId = run.BookId,
                    ChapterId = run.ChapterId,
                    AgentRole = run.CurrentRole,
                    MessageType = MessageType.SystemNote,
                    Content = $"❌ {reason} Use Continue (or Continue Planning) to pick up where it stopped. (run {run.Id})",
                    IsResolved = true
                });

                // Only reset in-memory state nobody has claimed since startup.
                if (_runState.GetStatus(run.BookId) is not { IsActive: true })
                {
                    _runState.SetStatus(run.BookId, new AgentRunStatus(run.CurrentRole, RunStates.Failed, run.ChapterId));
                    _runState.ClearStreamBuffers(run.BookId);
                }

                await notifier.NotifyAgentErrorAsync(run.BookId, run.CurrentRole.ToString(),
                    $"{reason} Use Continue to pick up where it stopped.", ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // One bad row must not stop the others from being recovered.
                _logger.LogError(ex, "[Book {BookId}] Failed to recover run {RunId}.", run.BookId, run.Id);
            }
        }

        _logger.LogInformation("RunRecoveryService: orphaned {Waiting} waiting and {Running} running run(s).",
            waiting.Count, running.Count);
    }
}
