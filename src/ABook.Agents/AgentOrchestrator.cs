using ABook.Core.Interfaces;
using ABook.Core.Models;

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

    public AgentOrchestrator(
        IBookRepository repo,
        PlannerAgent planner,
        WriterAgent writer,
        EditorAgent editor,
        ContinuityCheckerAgent continuity,
        AgentRunStateService state,
        IBookNotifier notifier)
    {
        _repo = repo;
        _planner = planner;
        _writer = writer;
        _editor = editor;
        _continuity = continuity;
        _state = state;
        _notifier = notifier;
    }

    public async Task StartPlanningAsync(int bookId, CancellationToken ct = default)
    {
        if (!_state.TryStartRun(bookId, AgentRole.Planner, null))
            throw new InvalidOperationException("An agent is already running for this book.");
        try
        {
            await _planner.PlanAsync(bookId, ct);
            _state.SetStatus(bookId, new AgentRunStatus(AgentRole.Planner, "Done", null));
        }
        catch
        {
            _state.SetStatus(bookId, new AgentRunStatus(AgentRole.Planner, "Failed", null));
            throw;
        }
    }

    public async Task StartWritingAsync(int bookId, int chapterId, CancellationToken ct = default)
    {
        if (!_state.TryStartRun(bookId, AgentRole.Writer, chapterId))
            throw new InvalidOperationException("An agent is already running for this book.");
        try
        {
            await _writer.WriteAsync(bookId, chapterId, ct);
            _state.SetStatus(bookId, new AgentRunStatus(AgentRole.Writer, "Done", chapterId));
        }
        catch
        {
            _state.SetStatus(bookId, new AgentRunStatus(AgentRole.Writer, "Failed", chapterId));
            throw;
        }
    }

    public async Task StartEditingAsync(int bookId, int chapterId, CancellationToken ct = default)
    {
        if (!_state.TryStartRun(bookId, AgentRole.Editor, chapterId))
            throw new InvalidOperationException("An agent is already running for this book.");
        try
        {
            await _editor.EditAsync(bookId, chapterId, ct);
            _state.SetStatus(bookId, new AgentRunStatus(AgentRole.Editor, "Done", chapterId));
        }
        catch
        {
            _state.SetStatus(bookId, new AgentRunStatus(AgentRole.Editor, "Failed", chapterId));
            throw;
        }
    }

    public async Task StartContinuityCheckAsync(int bookId, CancellationToken ct = default)
    {
        if (!_state.TryStartRun(bookId, AgentRole.ContinuityChecker, null))
            throw new InvalidOperationException("An agent is already running for this book.");
        try
        {
            await _continuity.CheckAsync(bookId, ct: ct);
            _state.SetStatus(bookId, new AgentRunStatus(AgentRole.ContinuityChecker, "Done", null));
        }
        catch
        {
            _state.SetStatus(bookId, new AgentRunStatus(AgentRole.ContinuityChecker, "Failed", null));
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
        }
        catch (OperationCanceledException)
        {
            var cur = _state.GetStatus(bookId);
            var cancelledRole = cur?.Role ?? AgentRole.Planner;
            _state.SetStatus(bookId, new AgentRunStatus(cancelledRole, "Cancelled", cur?.ChapterId));
            await _notifier.NotifyStatusChangedAsync(bookId, cancelledRole, "Cancelled", CancellationToken.None);
            await _notifier.NotifyWorkflowProgressAsync(bookId, "Workflow stopped.", true, CancellationToken.None);
            throw;
        }
        catch (Exception)
        {
            var cur = _state.GetStatus(bookId);
            var failedRole = cur?.Role ?? AgentRole.Planner;
            _state.SetStatus(bookId, new AgentRunStatus(failedRole, "Failed", cur?.ChapterId));
            await _notifier.NotifyStatusChangedAsync(bookId, failedRole, "Failed", CancellationToken.None);
            await _notifier.NotifyWorkflowProgressAsync(bookId, "Workflow failed.", true, CancellationToken.None);
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
        }
        catch (OperationCanceledException)
        {
            var cur = _state.GetStatus(bookId);
            var cancelledRole = cur?.Role ?? AgentRole.Writer;
            _state.SetStatus(bookId, new AgentRunStatus(cancelledRole, "Cancelled", cur?.ChapterId));
            await _notifier.NotifyStatusChangedAsync(bookId, cancelledRole, "Cancelled", CancellationToken.None);
            await _notifier.NotifyWorkflowProgressAsync(bookId, "Workflow stopped.", true, CancellationToken.None);
            throw;
        }
        catch (Exception)
        {
            var cur = _state.GetStatus(bookId);
            var failedRole = cur?.Role ?? AgentRole.Writer;
            _state.SetStatus(bookId, new AgentRunStatus(failedRole, "Failed", cur?.ChapterId));
            await _notifier.NotifyStatusChangedAsync(bookId, failedRole, "Failed", CancellationToken.None);
            await _notifier.NotifyWorkflowProgressAsync(bookId, "Workflow failed.", true, CancellationToken.None);
            throw;
        }
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
