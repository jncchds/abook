using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.Logging;

namespace ABook.Agents;

/// <summary>
/// Manages agent run lifecycle. Delegates to individual agents and
/// tracks per-book run state via the singleton AgentRunStateService.
/// </summary>
public class AgentOrchestrator : IAgentOrchestrator
{
    private readonly IBookRepository _repo;
    private readonly PlannerAgent _planner;
    private readonly WriterAgent _writer;
    private readonly EditorAgent _editor;
    private readonly ContinuityCheckerAgent _continuity;
    private readonly AgentRunStateService _state;
    private readonly IBookNotifier _notifier;
    private readonly ILogger<AgentOrchestrator> _logger;

    public AgentOrchestrator(
        IBookRepository repo,
        PlannerAgent planner,
        WriterAgent writer,
        EditorAgent editor,
        ContinuityCheckerAgent continuity,
        AgentRunStateService state,
        IBookNotifier notifier,
        ILogger<AgentOrchestrator> logger)
    {
        _repo = repo;
        _planner = planner;
        _writer = writer;
        _editor = editor;
        _continuity = continuity;
        _state = state;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task StartPlanningAsync(int bookId, CancellationToken ct = default)
    {
        if (!_state.TryStartRun(bookId, AgentRole.Planner, null))
            throw new InvalidOperationException("An agent is already running for this book.");
        _logger.LogInformation("[Book {BookId}] Planner started.", bookId);
        try
        {
            await _planner.PlanAsync(bookId, ct);
            _state.SetStatus(bookId, new AgentRunStatus(AgentRole.Planner, "Done", null));
            _logger.LogInformation("[Book {BookId}] Planner finished.", bookId);
        }
        catch (OperationCanceledException)
        {
            _state.SetStatus(bookId, new AgentRunStatus(AgentRole.Planner, "Failed", null));
            _logger.LogWarning("[Book {BookId}] Planner cancelled.", bookId);
            throw;
        }
        catch (Exception ex)
        {
            _state.SetStatus(bookId, new AgentRunStatus(AgentRole.Planner, "Failed", null));
            _logger.LogError(ex, "[Book {BookId}] Planner failed.", bookId);
            await ReportAgentErrorAsync(bookId, AgentRole.Planner, null, $"Planner failed: {ex.Message}");
            throw;
        }
    }

    public async Task StartWritingAsync(int bookId, int chapterId, CancellationToken ct = default)
    {
        if (!_state.TryStartRun(bookId, AgentRole.Writer, chapterId))
            throw new InvalidOperationException("An agent is already running for this book.");
        _logger.LogInformation("[Book {BookId}] Writer started for chapter {ChapterId}.", bookId, chapterId);
        try
        {
            await _writer.WriteAsync(bookId, chapterId, ct);
            _state.SetStatus(bookId, new AgentRunStatus(AgentRole.Writer, "Done", chapterId));
            _logger.LogInformation("[Book {BookId}] Writer finished chapter {ChapterId}.", bookId, chapterId);
        }
        catch (OperationCanceledException)
        {
            _state.SetStatus(bookId, new AgentRunStatus(AgentRole.Writer, "Failed", chapterId));
            _logger.LogWarning("[Book {BookId}] Writer cancelled for chapter {ChapterId}.", bookId, chapterId);
            throw;
        }
        catch (Exception ex)
        {
            _state.SetStatus(bookId, new AgentRunStatus(AgentRole.Writer, "Failed", chapterId));
            _logger.LogError(ex, "[Book {BookId}] Writer failed for chapter {ChapterId}.", bookId, chapterId);
            await ReportAgentErrorAsync(bookId, AgentRole.Writer, chapterId, $"Writer failed for chapter {chapterId}: {ex.Message}");
            throw;
        }
    }

    public async Task StartEditingAsync(int bookId, int chapterId, CancellationToken ct = default)
    {
        if (!_state.TryStartRun(bookId, AgentRole.Editor, chapterId))
            throw new InvalidOperationException("An agent is already running for this book.");
        _logger.LogInformation("[Book {BookId}] Editor started for chapter {ChapterId}.", bookId, chapterId);
        try
        {
            await _editor.EditAsync(bookId, chapterId, ct);
            _state.SetStatus(bookId, new AgentRunStatus(AgentRole.Editor, "Done", chapterId));
            _logger.LogInformation("[Book {BookId}] Editor finished chapter {ChapterId}.", bookId, chapterId);
        }
        catch (OperationCanceledException)
        {
            _state.SetStatus(bookId, new AgentRunStatus(AgentRole.Editor, "Failed", chapterId));
            _logger.LogWarning("[Book {BookId}] Editor cancelled for chapter {ChapterId}.", bookId, chapterId);
            throw;
        }
        catch (Exception ex)
        {
            _state.SetStatus(bookId, new AgentRunStatus(AgentRole.Editor, "Failed", chapterId));
            _logger.LogError(ex, "[Book {BookId}] Editor failed for chapter {ChapterId}.", bookId, chapterId);
            await ReportAgentErrorAsync(bookId, AgentRole.Editor, chapterId, $"Editor failed for chapter {chapterId}: {ex.Message}");
            throw;
        }
    }

    public async Task StartContinuityCheckAsync(int bookId, CancellationToken ct = default)
    {
        if (!_state.TryStartRun(bookId, AgentRole.ContinuityChecker, null))
            throw new InvalidOperationException("An agent is already running for this book.");
        _logger.LogInformation("[Book {BookId}] ContinuityChecker started.", bookId);
        try
        {
            await _continuity.CheckAsync(bookId, ct: ct);
            _state.SetStatus(bookId, new AgentRunStatus(AgentRole.ContinuityChecker, "Done", null));
            _logger.LogInformation("[Book {BookId}] ContinuityChecker finished.", bookId);
        }
        catch (OperationCanceledException)
        {
            _state.SetStatus(bookId, new AgentRunStatus(AgentRole.ContinuityChecker, "Failed", null));
            _logger.LogWarning("[Book {BookId}] ContinuityChecker cancelled.", bookId);
            throw;
        }
        catch (Exception ex)
        {
            _state.SetStatus(bookId, new AgentRunStatus(AgentRole.ContinuityChecker, "Failed", null));
            _logger.LogError(ex, "[Book {BookId}] ContinuityChecker failed.", bookId);
            await ReportAgentErrorAsync(bookId, AgentRole.ContinuityChecker, null, $"Continuity checker failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Full autonomous workflow: Plan → Write+Edit each chapter → Continuity check.
    /// Agents may pause to ask the user questions; resume via ResumeWithAnswerAsync.
    /// </summary>
    public async Task StartWorkflowAsync(int bookId, CancellationToken ct = default)
    {
        if (!_state.TryStartRun(bookId, AgentRole.Planner, null))
            throw new InvalidOperationException("An agent is already running for this book.");
        _logger.LogInformation("[Book {BookId}] Full workflow started.", bookId);
        try
        {
            // 1. Plan
            await _notifier.NotifyWorkflowProgressAsync(bookId, "Starting planning…", false, ct);
            var chapters = await _planner.PlanAsync(bookId, ct);
            await _notifier.NotifyWorkflowProgressAsync(bookId,
                $"Planning complete — {chapters.Count} chapter{(chapters.Count == 1 ? "" : "s")} outlined.", false, ct);

            // 2. Write → Continuity check → Edit each chapter in order
            foreach (var chapter in chapters.OrderBy(c => c.Number))
            {
                ct.ThrowIfCancellationRequested();

                // Pre-write check: auto-fix outline if it contradicts established facts
                _state.UpdateRunRole(bookId, AgentRole.ContinuityChecker, chapter.Id);
                await _notifier.NotifyWorkflowProgressAsync(bookId,
                    $"Pre-write review for Chapter {chapter.Number}…", false, ct);
                await _continuity.PreWriteCheckAndFixAsync(bookId, chapter.Id, ct);

                ct.ThrowIfCancellationRequested();

                _state.UpdateRunRole(bookId, AgentRole.Writer, chapter.Id);
                await _notifier.NotifyWorkflowProgressAsync(bookId,
                    $"Writing Chapter {chapter.Number}: {chapter.Title}…", false, ct);
                await _writer.WriteAsync(bookId, chapter.Id, ct);

                ct.ThrowIfCancellationRequested();

                _state.UpdateRunRole(bookId, AgentRole.ContinuityChecker, chapter.Id);
                await _notifier.NotifyWorkflowProgressAsync(bookId,
                    $"Checking continuity for Chapter {chapter.Number}…", false, ct);
                var continuityReport = await _continuity.CheckAsync(bookId, chapter.Id, ct);

                ct.ThrowIfCancellationRequested();

                _state.UpdateRunRole(bookId, AgentRole.Editor, chapter.Id);
                await _notifier.NotifyWorkflowProgressAsync(bookId,
                    $"Editing Chapter {chapter.Number}…", false, ct);
                await _editor.EditAsync(bookId, chapter.Id, ct, continuityReport);

                await _notifier.NotifyWorkflowProgressAsync(bookId,
                    $"Chapter {chapter.Number} complete.", false, ct);
            }

            // 3. Final full-manuscript continuity check
            ct.ThrowIfCancellationRequested();
            _state.UpdateRunRole(bookId, AgentRole.ContinuityChecker, null);
            await _notifier.NotifyWorkflowProgressAsync(bookId, "Running final continuity check…", false, ct);
            await _continuity.CheckAsync(bookId, ct: ct);

            _state.SetStatus(bookId, new AgentRunStatus(AgentRole.ContinuityChecker, "Done", null));
            await _notifier.NotifyWorkflowProgressAsync(bookId, "Workflow complete!", true, ct);
            _logger.LogInformation("[Book {BookId}] Full workflow completed.", bookId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var cur = _state.GetStatus(bookId);
            var cancelledRole = cur?.Role ?? AgentRole.Planner;
            _state.SetStatus(bookId, new AgentRunStatus(cancelledRole, "Cancelled", cur?.ChapterId));
            await _notifier.NotifyStatusChangedAsync(bookId, cancelledRole, "Cancelled", CancellationToken.None);
            await _notifier.NotifyWorkflowProgressAsync(bookId, "Workflow stopped.", true, CancellationToken.None);
            _logger.LogWarning("[Book {BookId}] Full workflow stopped by user.", bookId);
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // Not user-initiated — likely an LLM timeout or connection failure
            var cur = _state.GetStatus(bookId);
            var failedRole = cur?.Role ?? AgentRole.Planner;
            _state.SetStatus(bookId, new AgentRunStatus(failedRole, "Failed", cur?.ChapterId));
            await _notifier.NotifyStatusChangedAsync(bookId, failedRole, "Failed", CancellationToken.None);
            await ReportAgentErrorAsync(bookId, failedRole, cur?.ChapterId,
                $"Request cancelled unexpectedly — possible LLM timeout or connection issue. Detail: {ex.Message}");
            await _notifier.NotifyWorkflowProgressAsync(bookId, "Workflow failed (request cancelled).", true, CancellationToken.None);
            _logger.LogError(ex, "[Book {BookId}] Full workflow failed due to unexpected cancellation (not user-initiated).", bookId);
            throw;
        }
        catch (Exception ex)
        {
            var cur = _state.GetStatus(bookId);
            var failedRole = cur?.Role ?? AgentRole.Planner;
            _state.SetStatus(bookId, new AgentRunStatus(failedRole, "Failed", cur?.ChapterId));
            await _notifier.NotifyStatusChangedAsync(bookId, failedRole, "Failed", CancellationToken.None);
            await _notifier.NotifyWorkflowProgressAsync(bookId, "Workflow failed.", true, CancellationToken.None);
            await ReportAgentErrorAsync(bookId, failedRole, cur?.ChapterId, $"{failedRole} failed: {ex.Message}");
            _logger.LogError(ex, "[Book {BookId}] Full workflow failed.", bookId);
            throw;
        }
    }

    public Task StopWorkflowAsync(int bookId)
    {
        _state.CancelRun(bookId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Continue an interrupted workflow: skips Done chapters, resumes writing/editing
    /// from where it left off, then runs the continuity check.
    /// </summary>
    public async Task ContinueWorkflowAsync(int bookId, CancellationToken ct = default)
    {
        if (!_state.TryStartRun(bookId, AgentRole.Writer, null))
            throw new InvalidOperationException("An agent is already running for this book.");
        try
        {
            var book = await _repo.GetByIdWithDetailsAsync(bookId)
                ?? throw new InvalidOperationException($"Book {bookId} not found.");

            var allChapters = book.Chapters.OrderBy(c => c.Number).ToList();
            if (allChapters.Count == 0)
                throw new InvalidOperationException("No chapters to continue. Run the planner first.");

            await _notifier.NotifyWorkflowProgressAsync(bookId, "Continuing workflow…", false, ct);

            foreach (var chapterRef in allChapters)
            {
                ct.ThrowIfCancellationRequested();

                // Re-fetch from DB so we see the real current status, not the stale snapshot
                var chapter = await _repo.GetChapterAsync(bookId, chapterRef.Id)
                    ?? throw new InvalidOperationException($"Chapter {chapterRef.Id} not found.");

                if (chapter.Status == ChapterStatus.Done)
                {
                    await _notifier.NotifyWorkflowProgressAsync(bookId,
                        $"Chapter {chapter.Number} already done — skipping.", false, ct);
                    continue;
                }

                // Write if not yet written, or if a previous run was interrupted during writing
                bool needsWrite = chapter.Status == ChapterStatus.Outlined
                    || chapter.Status == ChapterStatus.Writing
                    || string.IsNullOrEmpty(chapter.Content);

                if (needsWrite)
                {
                    // Pre-write check: auto-fix outline contradictions before writing
                    _state.UpdateRunRole(bookId, AgentRole.ContinuityChecker, chapter.Id);
                    await _notifier.NotifyWorkflowProgressAsync(bookId,
                        $"Pre-write review for Chapter {chapter.Number}…", false, ct);
                    await _continuity.PreWriteCheckAndFixAsync(bookId, chapter.Id, ct);
                    ct.ThrowIfCancellationRequested();

                    _state.UpdateRunRole(bookId, AgentRole.Writer, chapter.Id);
                    await _notifier.NotifyWorkflowProgressAsync(bookId,
                        $"Writing Chapter {chapter.Number}: {chapter.Title}…", false, ct);
                    await _writer.WriteAsync(bookId, chapter.Id, ct);
                    ct.ThrowIfCancellationRequested();
                }

                // Re-read status; after write chapter is Review — needs continuity + edit
                // After an interrupted edit chapter is Editing — only needs edit
                var latestStatus = (await _repo.GetChapterAsync(bookId, chapter.Id))?.Status
                    ?? ChapterStatus.Outlined;

                // Continuity check if chapter was just written (Review) or continuity was interrupted
                bool needsContinuity = latestStatus == ChapterStatus.Review;
                string? continuityReport = null;
                if (needsContinuity)
                {
                    _state.UpdateRunRole(bookId, AgentRole.ContinuityChecker, chapter.Id);
                    await _notifier.NotifyWorkflowProgressAsync(bookId,
                        $"Checking continuity for Chapter {chapter.Number}…", false, ct);
                    continuityReport = await _continuity.CheckAsync(bookId, chapter.Id, ct);
                    ct.ThrowIfCancellationRequested();
                    latestStatus = (await _repo.GetChapterAsync(bookId, chapter.Id))?.Status
                        ?? latestStatus;
                }

                if (latestStatus != ChapterStatus.Done)
                {
                    _state.UpdateRunRole(bookId, AgentRole.Editor, chapter.Id);
                    await _notifier.NotifyWorkflowProgressAsync(bookId,
                        $"Editing Chapter {chapter.Number}…", false, ct);
                    await _editor.EditAsync(bookId, chapter.Id, ct, continuityReport);
                }

                await _notifier.NotifyWorkflowProgressAsync(bookId,
                    $"Chapter {chapter.Number} complete.", false, ct);
            }

            ct.ThrowIfCancellationRequested();
            _state.UpdateRunRole(bookId, AgentRole.ContinuityChecker, null);
            await _notifier.NotifyWorkflowProgressAsync(bookId, "Running continuity check…", false, ct);
            await _continuity.CheckAsync(bookId, ct: ct);

            _state.SetStatus(bookId, new AgentRunStatus(AgentRole.ContinuityChecker, "Done", null));
            await _notifier.NotifyWorkflowProgressAsync(bookId, "Workflow complete!", true, ct);
            _logger.LogInformation("[Book {BookId}] Continue-workflow completed.", bookId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var cur = _state.GetStatus(bookId);
            var cancelledRole = cur?.Role ?? AgentRole.Writer;
            _state.SetStatus(bookId, new AgentRunStatus(cancelledRole, "Cancelled", cur?.ChapterId));
            await _notifier.NotifyStatusChangedAsync(bookId, cancelledRole, "Cancelled", CancellationToken.None);
            await _notifier.NotifyWorkflowProgressAsync(bookId, "Workflow stopped.", true, CancellationToken.None);
            _logger.LogWarning("[Book {BookId}] Continue-workflow stopped by user.", bookId);
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // Not user-initiated — likely an LLM timeout or connection failure
            var cur = _state.GetStatus(bookId);
            var failedRole = cur?.Role ?? AgentRole.Writer;
            _state.SetStatus(bookId, new AgentRunStatus(failedRole, "Failed", cur?.ChapterId));
            await _notifier.NotifyStatusChangedAsync(bookId, failedRole, "Failed", CancellationToken.None);
            await ReportAgentErrorAsync(bookId, failedRole, cur?.ChapterId,
                $"Request cancelled unexpectedly — possible LLM timeout or connection issue. Detail: {ex.Message}");
            await _notifier.NotifyWorkflowProgressAsync(bookId, "Workflow failed (request cancelled).", true, CancellationToken.None);
            _logger.LogError(ex, "[Book {BookId}] Continue-workflow failed due to unexpected cancellation (not user-initiated).", bookId);
            throw;
        }
        catch (Exception ex)
        {
            var cur = _state.GetStatus(bookId);
            var failedRole = cur?.Role ?? AgentRole.Writer;
            _state.SetStatus(bookId, new AgentRunStatus(failedRole, "Failed", cur?.ChapterId));
            await _notifier.NotifyStatusChangedAsync(bookId, failedRole, "Failed", CancellationToken.None);
            await _notifier.NotifyWorkflowProgressAsync(bookId, "Workflow failed.", true, CancellationToken.None);
            await ReportAgentErrorAsync(bookId, failedRole, cur?.ChapterId, $"{failedRole} failed: {ex.Message}");
            _logger.LogError(ex, "[Book {BookId}] Continue-workflow failed.", bookId);
            throw;
        }
    }

    /// <summary>
    /// Persists an error as a chat-visible SystemNote message AND fires the AgentError SignalR event.
    /// </summary>
    private async Task ReportAgentErrorAsync(int bookId, AgentRole role, int? chapterId, string message)
    {
        try
        {
            await _repo.AddMessageAsync(new AgentMessage
            {
                BookId = bookId,
                ChapterId = chapterId,
                AgentRole = role,
                MessageType = MessageType.SystemNote,
                Content = $"\u274c {message}",
                IsResolved = true
            });
        }
        catch { /* non-fatal */ }
        try { await _notifier.NotifyAgentErrorAsync(bookId, role.ToString(), message, CancellationToken.None); }
        catch { /* non-fatal */ }
    }

    public async Task ResumeWithAnswerAsync(int messageId, string answer, CancellationToken ct = default)
    {
        // Find the message to get bookId
        var messages = await _repo.FindMessageByIdAsync(messageId);
        if (messages is null) return;

        // Mark message resolved in DB
        messages.IsResolved = true;
        await _repo.UpdateMessageAsync(messages);

        // Persist answer
        await _repo.AddMessageAsync(new AgentMessage
        {
            BookId = messages.BookId,
            ChapterId = messages.ChapterId,
            AgentRole = messages.AgentRole,
            MessageType = MessageType.Answer,
            Content = answer,
            IsResolved = true
        });

        // Unblock the waiting agent
        _state.TryResolvePending(messages.BookId, messageId, answer);
    }

    public Task<AgentRunStatus?> GetRunStatusAsync(int bookId) =>
        Task.FromResult(_state.GetStatus(bookId));
}
