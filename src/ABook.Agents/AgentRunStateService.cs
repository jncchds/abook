using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace ABook.Agents;

/// <summary>
/// Singleton service that tracks per-book agent run state across HTTP requests.
/// Keeps fast in-memory state for active runs and persists key lifecycle events
/// (start, pause, resume, finish) to the database so runs interrupted by a process restart
/// can be detected and closed out (see <c>RunRecoveryService</c>).
/// <para>
/// <b>Scaling limitation:</b> This service is process-local (in-memory). It will not work
/// correctly with horizontal scaling (multiple app instances / Kubernetes pods). For production
/// multi-instance deployments, replace with a distributed state backend (e.g., Redis).
/// </para>
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

    // bookId → planning run waiting for every open clarification to be answered
    private readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> _clarificationWaiters = new();

    // Per-book cancellation tokens (for workflow stop)
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _cts = new();

    // bookId -> persisted AgentRun.Id
    private readonly ConcurrentDictionary<int, Guid> _runIds = new();

    // Per-streaming-call token accumulation buffers: (bookId, chapterId, agentRole) -> StringBuilder
    // Reused as the actual `sb` in StreamResponseAsync so there is no duplication.
    // Cleared when a run reaches a terminal state.
    private readonly ConcurrentDictionary<(int BookId, int? ChapterId, string AgentRole), StreamBuffer> _streamBuffers = new();

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

    /// <summary>
    /// Atomically claims the book and a concurrency slot, marks the run Running and creates its
    /// cancellation token. Doing all of this under one lock is what makes a double-click (or a UI
    /// and an MCP client starting at once) safe: exactly one caller gets <see cref="RunStartResult.Success"/>.
    /// </summary>
    public RunStartResult TryStartRun(int bookId, AgentRole role, int? chapterId, out CancellationToken ct)
    {
        lock (_startLock)
        {
            ct = CancellationToken.None;

            if (_status.GetValueOrDefault(bookId) is { IsActive: true })
                return RunStartResult.BookBusy;

            if (_status.Values.Count(s => s.IsActive) >= MaxConcurrentRuns)
                return RunStartResult.AtCapacity;

            _status[bookId] = new AgentRunStatus(role, RunStates.Running, chapterId);

            // Any previous source belongs to a finished run — nothing can still be observing it.
            if (_cts.TryRemove(bookId, out var old)) old.Dispose();
            var cts = new CancellationTokenSource();
            _cts[bookId] = cts;
            ct = cts.Token;
            return RunStartResult.Success;
        }
    }

    /// <summary>Disposes the run's cancellation source once the run has ended, unless a newer run replaced it.</summary>
    public void ReleaseRunCts(int bookId, CancellationToken runToken)
    {
        if (_cts.TryGetValue(bookId, out var cts) && cts.Token == runToken
            && _cts.TryRemove(new KeyValuePair<int, CancellationTokenSource>(bookId, cts)))
            cts.Dispose();
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

    /// <summary>
    /// Persist the run in WaitingForInput state with the question message id — null while it waits for
    /// the clarifications, which are answered on their own page rather than through a message.
    /// </summary>
    public async Task PersistRunPausedAsync(int bookId, int? messageId, string? workflowContext = null)
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
    /// Returns (or creates) the buffer that AgentBase accumulates into in <c>StreamResponseAsync</c>.
    /// The same object is both written to during streaming and read back by the HTTP endpoint,
    /// so no extra copy is needed.
    /// </summary>
    public StreamBuffer GetOrCreateStreamBuffer(int bookId, int? chapterId, string agentRole) =>
        _streamBuffers.GetOrAdd((bookId, chapterId, agentRole), _ => new StreamBuffer());

    /// <summary>Returns the accumulated content for a specific streaming call, or null if not found.</summary>
    public string? GetStreamBufferContent(int bookId, int? chapterId, string? agentRole)
    {
        if (agentRole is not null)
            return _streamBuffers.TryGetValue((bookId, chapterId, agentRole), out var sb) ? sb.ToString() : null;

        // No role specified — return the first non-empty buffer for this book+chapter combination
        foreach (var kvp in _streamBuffers)
            if (kvp.Key.BookId == bookId && kvp.Key.ChapterId == chapterId && kvp.Value.Length > 0)
                return kvp.Value.ToString();
        return null;
    }

    /// <summary>Removes all stream buffers for a book. Call when the run reaches a terminal state.</summary>
    public void ClearStreamBuffers(int bookId)
    {
        foreach (var key in _streamBuffers.Keys.Where(k => k.BookId == bookId).ToList())
            _streamBuffers.TryRemove(key, out _);
    }

    // ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Waits for the author to answer every open clarification on the Clarifications page. Completes
    /// when <see cref="TryResolveClarifications"/> is called — true when the round is complete, false
    /// when the author reset it — and is cancelled by <see cref="CancelRun"/>.
    /// </summary>
    public Task<bool> WaitForClarificationsAsync(int bookId)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _clarificationWaiters[bookId] = tcs;
        return tcs.Task;
    }

    /// <summary>Releases a run waiting for clarification answers. False when no run is waiting.</summary>
    public bool TryResolveClarifications(int bookId, bool roundComplete = true) =>
        _clarificationWaiters.TryRemove(bookId, out var tcs) && tcs.TrySetResult(roundComplete);

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

    /// <summary>
    /// Cancels the running workflow/agent for a book and unblocks any pending question. Returns false
    /// when nothing was running. As a fail-safe, a book whose status still says active although no run
    /// owns it any more (no cancellation source) is forced to Cancelled so it can never stay locked.
    /// </summary>
    public bool CancelRun(int bookId)
    {
        bool cancelled = false;
        if (_cts.TryGetValue(bookId, out var cts))
        {
            // Not disposed here: the run's own finally (ReleaseRunCts) does that once it has unwound.
            try { cts.Cancel(); cancelled = true; }
            catch (ObjectDisposedException) { }
        }
        // Unblock any agent waiting for a user answer
        if (_pending.TryRemove(bookId, out var pending))
            pending.Tcs.TrySetCanceled();
        if (_clarificationWaiters.TryRemove(bookId, out var waiter))
            waiter.TrySetCanceled();

        if (!cancelled && _status.GetValueOrDefault(bookId) is { IsActive: true } stuck)
        {
            _logger.LogWarning("[Book {BookId}] Stop requested for an active status with no live run — forcing Cancelled.", bookId);
            _status[bookId] = stuck with { State = RunStates.Cancelled };
            ClearStreamBuffers(bookId);
            _runIds.TryRemove(bookId, out _);
            cancelled = true;
        }
        return cancelled;
    }

    /// <summary>Updates the active role/chapter while keeping state = Running (used mid-workflow).</summary>
    public void UpdateRunRole(int bookId, AgentRole role, int? chapterId) =>
        _status[bookId] = new AgentRunStatus(role, RunStates.Running, chapterId);
}

/// <summary>
/// Accumulated content of one streaming LLM call. Written by the agent while it streams and read
/// concurrently by the HTTP endpoint that restores a stream after a page refresh, so every access
/// is serialised — a bare <see cref="System.Text.StringBuilder"/> can throw or return torn text
/// when read during an append.
/// </summary>
public sealed class StreamBuffer
{
    private readonly System.Text.StringBuilder _sb = new();
    private readonly object _lock = new();

    public int Length { get { lock (_lock) return _sb.Length; } }

    public void Append(string text) { lock (_lock) _sb.Append(text); }

    public void Clear() { lock (_lock) _sb.Clear(); }

    /// <summary>Replaces the whole content.</summary>
    public void Set(string text)
    {
        lock (_lock) { _sb.Clear(); _sb.Append(text); }
    }

    public override string ToString() { lock (_lock) return _sb.ToString(); }

    /// <summary>
    /// The last (at most) <paramref name="maxChars"/> characters, trimmed forward to the next line start
    /// so a line-anchored regex never sees a partial line as if it began there.
    /// </summary>
    public string TailFromLineStart(int maxChars)
    {
        lock (_lock)
        {
            if (_sb.Length <= maxChars) return _sb.ToString();
            var tail = _sb.ToString(_sb.Length - maxChars, maxChars);
            var nl = tail.IndexOf('\n');
            return nl < 0 ? string.Empty : tail[(nl + 1)..];
        }
    }
}
