using ABook.Agents;
using ABook.Core.Interfaces;
using ABook.Core.Models;

namespace ABook.Api.HostedServices;

/// <summary>
/// Background service that runs once at startup and recovers any agent runs that were
/// left in a non-terminal state (Running or WaitingForInput) by a previous process.
///
/// Recovery strategy:
/// - WaitingForInput: rehydrate the in-memory AgentRunStateService with a fresh TCS so
///   when the user submits their answer the TCS is resolved normally, resuming the
///   already-running background task. The run remains in WaitingForInput until answered.
/// - Running: mark as Orphaned — the actual LLM call was lost when the process died.
///   These are surfaced to the admin/user via the UI (status shows as orphaned) so they
///   can restart the workflow manually.
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

        // Recover WaitingForInput runs: rehydrate the TCS so answers can resume them
        var waiting = (await repo.GetRunsByStatusAsync(AgentRunPersistStatus.WaitingForInput)).ToList();
        foreach (var run in waiting)
        {
            if (run.PendingMessageId is null)
            {
                _logger.LogWarning("[Book {BookId}] WaitingForInput run {RunId} has no PendingMessageId — marking Orphaned.", run.BookId, run.Id);
                run.Status = AgentRunPersistStatus.Orphaned;
                await repo.UpdateRunAsync(run);
                continue;
            }

            _logger.LogInformation("[Book {BookId}] Rehydrating WaitingForInput run {RunId} (message {MessageId}).",
                run.BookId, run.Id, run.PendingMessageId);

            _runState.RehydrateWaitingRun(
                run.BookId, run.Id, run.CurrentRole, run.ChapterId, run.PendingMessageId.Value);

            // Notify the UI that there is still a pending question
            try
            {
                var msg = await repo.FindMessageByIdAsync(run.PendingMessageId.Value);
                if (msg is not null && !msg.IsResolved)
                    await notifier.NotifyQuestionAsync(run.BookId, msg, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Book {BookId}] Could not re-notify pending question for run {RunId}.", run.BookId, run.Id);
            }

            _logger.LogInformation("[Book {BookId}] Run {RunId} rehydrated — waiting for user answer.", run.BookId, run.Id);
        }

        // Orphan Running runs — their LLM tasks are gone; user must restart
        var stuckRunning = (await repo.GetRunsByStatusAsync(AgentRunPersistStatus.Running)).ToList();
        foreach (var run in stuckRunning)
        {
            _logger.LogWarning("[Book {BookId}] Found orphaned Running run {RunId} ({RunType}) — marking Orphaned.", run.BookId, run.Id, run.RunType);
            run.Status = AgentRunPersistStatus.Orphaned;
            await repo.UpdateRunAsync(run);

            // Reset in-memory status so the user can start a new run
            _runState.SetStatus(run.BookId, new AgentRunStatus(run.CurrentRole, "Failed", run.ChapterId));

            try
            {
                await repo.AddMessageAsync(new AgentMessage
                {
                    BookId = run.BookId,
                    ChapterId = run.ChapterId,
                    AgentRole = run.CurrentRole,
                    MessageType = MessageType.SystemNote,
                    Content = $"❌ Agent run was interrupted by a server restart (run {run.Id}). Please start the workflow again.",
                    IsResolved = true
                });
                await notifier.NotifyAgentErrorAsync(run.BookId, run.CurrentRole.ToString(),
                    "Agent run was interrupted by a server restart. Please start the workflow again.", ct);
            }
            catch { /* non-fatal */ }
        }

        _logger.LogInformation("RunRecoveryService: recovered {Waiting} waiting run(s), orphaned {Stuck} stuck run(s).",
            waiting.Count(r => r.Status == AgentRunPersistStatus.WaitingForInput),
            stuckRunning.Count);
    }
}
