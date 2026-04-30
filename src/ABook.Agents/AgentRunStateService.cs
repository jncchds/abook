using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace ABook.Agents;

/// <summary>
/// Singleton service that tracks per-book agent run state across HTTP requests.
/// Keeps fast in-memory state for active runs and persists key lifecycle events
/// (start, pause, resume, finish) to the database so runs can be recovered after
/// a process restart.
/// </summary>
public class AgentRunStateService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AgentRunStateService> _logger;
    private readonly object _startLock = new();
    private int _maxConcurrentRuns = 3;

    private readonly ConcurrentDictionary<int, AgentRunStatus> _status = new();

    // bookId -> (messageId, TaskCompletionSource for answer)
    private readonly ConcurrentDictionary<int, (int MessageId, TaskCompletionSource<string> Tcs)> _pending = new();

    // Per-book cancellation tokens (for workflow stop)
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _cts = new();

    // bookId -> persisted AgentRun.Id
    private readonly ConcurrentDictionary<int, Guid> _runIds = new();

    // Per-streaming-call token accumulation buffers: (bookId, chapterId, agentRole) -> StringBuilder
    // Reused as the actual `sb` in StreamResponseAsync so there is no duplication.
    // Cleared when a run reaches a terminal state.
    private readonly ConcurrentDictionary<(int BookId, int? ChapterId, string AgentRole), System.Text.StringBuilder> _streamBuffers = new();

    public AgentRunStateService(IServiceScopeFactory scopeFactory, ILogger<AgentRunStateService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public AgentRunStatus? GetStatus(int bookId) =>
        _status.TryGetValue(bookId, out var s) ? s : null;

    public int MaxConcurrentRuns
    {
        get => _maxConcurrentRuns;
        set => _maxConcurrentRuns = Math.Max(1, value);
    }

    public void SetStatus(int bookId, AgentRunStatus status) =>
        _status[bookId] = status;

    public bool IsAtCapacity()
    {
        var activeRuns = _status.Values.Count(s => s.State is "Running" or "WaitingForInput");
        return activeRuns >= MaxConcurrentRuns;
    }

    public RunStartResult TryStartRun(int bookId, AgentRole role, int? chapterId)
    {
        lock (_startLock)
        {
            var current = _status.GetValueOrDefault(bookId);
            if (current is { State: "Running" or "WaitingForInput" })
                return RunStartResult.BookBusy;

            var activeRuns = _status.Values.Count(s => s.State is "Running" or "WaitingForInput");
            if (activeRuns >= MaxConcurrentRuns)
                return RunStartResult.AtCapacity;

            _status[bookId] = new AgentRunStatus(role, "Running", chapterId);
            return RunStartResult.Success;
        }
    }

    /// <summary>
    /// Persist a new AgentRun row and cache the run Id for subsequent updates.
    /// Call immediately after TryStartRun succeeds.
    /// </summary>
    public async Task PersistRunStartAsync(int bookId, AgentRole role, int? chapterId, string runType)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IBookRepository>();

            // Mark any previous active run from prior process as orphaned
            var stale = await repo.GetActiveRunForBookAsync(bookId);
            if (stale is not null)
            {
                stale.Status = AgentRunPersistStatus.Orphaned;
                await repo.UpdateRunAsync(stale);
            }

            var run = await repo.CreateRunAsync(new AgentRun
            {
                BookId = bookId,
                RunType = runType,
                Status = AgentRunPersistStatus.Running,
                CurrentRole = role,
                ChapterId = chapterId
            });
            _runIds[bookId] = run.Id;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Book {BookId}] Failed to persist run start — continuing without persistence.", bookId);
        }
    }

    /// <summary>Persist the run in WaitingForInput state with the question message id.</summary>
    public async Task PersistRunPausedAsync(int bookId, int messageId, string? workflowContext = null)
    {
        if (!_runIds.TryGetValue(bookId, out var runId)) return;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IBookRepository>();
            var run = await repo.GetRunByIdAsync(runId);
            if (run is null) return;
            run.Status = AgentRunPersistStatus.WaitingForInput;
            run.PendingMessageId = messageId;
            if (workflowContext is not null) run.WorkflowContext = workflowContext;
            await repo.UpdateRunAsync(run);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Book {BookId}] Failed to persist run pause.", bookId);
        }
    }

    /// <summary>Persist the run back to Running after an answer is received.</summary>
    public async Task PersistRunResumedAsync(int bookId)
    {
        if (!_runIds.TryGetValue(bookId, out var runId)) return;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IBookRepository>();
            var run = await repo.GetRunByIdAsync(runId);
            if (run is null) return;
            run.Status = AgentRunPersistStatus.Running;
            run.PendingMessageId = null;
            await repo.UpdateRunAsync(run);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Book {BookId}] Failed to persist run resume.", bookId);
        }
    }

    /// <summary>Persist terminal state (Completed / Failed / Cancelled) and clean up in-memory run id.</summary>
    public async Task PersistRunFinishedAsync(int bookId, AgentRunPersistStatus finalStatus)
    {
        ClearStreamBuffers(bookId);
        if (!_runIds.TryRemove(bookId, out var runId)) return;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IBookRepository>();
            var run = await repo.GetRunByIdAsync(runId);
            if (run is null) return;
            run.Status = finalStatus;
            run.PendingMessageId = null;
            await repo.UpdateRunAsync(run);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Book {BookId}] Failed to persist run finish ({Status}).", bookId, finalStatus);
        }
    }

    // ─── Stream buffer helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Returns (or creates) the StringBuilder that AgentBase uses as <c>sb</c> in
    /// <c>StreamResponseAsync</c>. The same object is both written to during streaming
    /// and read back by the HTTP endpoint, so no extra copy is needed.
    /// </summary>
    public System.Text.StringBuilder GetOrCreateStreamBuffer(int bookId, int? chapterId, string agentRole) =>
        _streamBuffers.GetOrAdd((bookId, chapterId, agentRole), _ => new System.Text.StringBuilder());

    /// <summary>Returns the accumulated content for a specific streaming call, or null if not found.</summary>
    public string? GetStreamBufferContent(int bookId, int? chapterId, string? agentRole)
    {
        if (agentRole is null) return null;
        return _streamBuffers.TryGetValue((bookId, chapterId, agentRole), out var sb) ? sb.ToString() : null;
    }

    /// <summary>Removes all stream buffers for a book. Call when the run reaches a terminal state.</summary>
    public void ClearStreamBuffers(int bookId)
    {
        foreach (var key in _streamBuffers.Keys.Where(k => k.BookId == bookId).ToList())
            _streamBuffers.TryRemove(key, out _);
    }

    // ────────────────────────────────────────────────────────────────────────────

    public void SetPending(int bookId, int messageId, TaskCompletionSource<string> tcs) =>
        _pending[bookId] = (messageId, tcs);

    public bool TryResolvePending(int bookId, int messageId, string answer)
    {
        if (_pending.TryGetValue(bookId, out var pending) && pending.MessageId == messageId)
        {
            _pending.TryRemove(bookId, out _);
            pending.Tcs.TrySetResult(answer);
            return true;
        }
        return false;
    }

    public bool HasPending(int bookId) => _pending.ContainsKey(bookId);

    /// <summary>Creates a new CancellationToken for a book's run. Disposes any previous CTS.</summary>
    public CancellationToken CreateRunCts(int bookId)
    {
        if (_cts.TryRemove(bookId, out var old)) old.Dispose();
        var cts = new CancellationTokenSource();
        _cts[bookId] = cts;
        return cts.Token;
    }

    /// <summary>Cancels the running workflow/agent for a book and unblocks any pending question.</summary>
    public void CancelRun(int bookId)
    {
        if (_cts.TryRemove(bookId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
        // Unblock any agent waiting for a user answer
        if (_pending.TryRemove(bookId, out var pending))
            pending.Tcs.TrySetCanceled();
    }

    /// <summary>Updates the active role/chapter while keeping state = Running (used mid-workflow).</summary>
    public void UpdateRunRole(int bookId, AgentRole role, int? chapterId) =>
        _status[bookId] = new AgentRunStatus(role, "Running", chapterId);

    /// <summary>
    /// Reconstitute in-memory state from a persisted WaitingForInput run after process restart.
    /// Called by RunRecoveryService. Creates a fresh TCS that will be resolved when the user answers.
    /// </summary>
    public void RehydrateWaitingRun(int bookId, Guid runId, AgentRole role, int? chapterId, int pendingMessageId)
    {
        _status[bookId] = new AgentRunStatus(role, "WaitingForInput", chapterId);
        _runIds[bookId] = runId;

        // Register a fresh TCS so ResumeWithAnswerAsync can unblock it when the answer arrives
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[bookId] = (pendingMessageId, tcs);
    }

    /// <summary>Returns the TCS task for a rehydrated waiting run, so the recovery service can await it.</summary>
    public Task<string>? GetPendingTask(int bookId) =>
        _pending.TryGetValue(bookId, out var p) ? p.Tcs.Task : null;
}
