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
    private readonly QuestionAgent _questions;
    private readonly StoryBibleAgent _storyBibleAgent;
    private readonly CharactersAgent _charactersAgent;
    private readonly PlotThreadsAgent _plotThreadsAgent;
    private readonly PlannerAgent _planner;
    private readonly WriterAgent _writer;
    private readonly EditorAgent _editor;
    private readonly ContinuityCheckerAgent _continuity;
    private readonly AgentRunStateService _state;
    private readonly IBookNotifier _notifier;
    private readonly ILogger<AgentOrchestrator> _logger;

    public AgentOrchestrator(
        IBookRepository repo,
        QuestionAgent questions,
        StoryBibleAgent storyBibleAgent,
        CharactersAgent charactersAgent,
        PlotThreadsAgent plotThreadsAgent,
        PlannerAgent planner,
        WriterAgent writer,
        EditorAgent editor,
        ContinuityCheckerAgent continuity,
        AgentRunStateService state,
        IBookNotifier notifier,
        ILogger<AgentOrchestrator> logger)
    {
        _repo = repo;
        _questions = questions;
        _storyBibleAgent = storyBibleAgent;
        _charactersAgent = charactersAgent;
        _plotThreadsAgent = plotThreadsAgent;
        _planner = planner;
        _writer = writer;
        _editor = editor;
        _continuity = continuity;
        _state = state;
        _notifier = notifier;
        _logger = logger;
    }

    public Task StartPlanningAsync(int bookId, CancellationToken ct = default) =>
        ExecuteAgentRunAsync(bookId, AgentRole.Planner, null, ct, async c =>
        {
            var book = await _repo.GetByIdAsync(bookId)
                ?? throw new InvalidOperationException($"Book {bookId} not found.");

            bool skipSb      = book.StoryBibleStatus   == PlanningPhaseStatus.Complete;
            bool skipChars   = book.CharactersStatus   == PlanningPhaseStatus.Complete;
            bool skipThreads = book.PlotThreadsStatus  == PlanningPhaseStatus.Complete;
            bool skipChapters = book.ChaptersStatus    == PlanningPhaseStatus.Complete;

            if (skipSb && skipChars && skipThreads && skipChapters)
            {
                await _notifier.NotifyWorkflowProgressAsync(bookId,
                    "All planning phases are already complete. Use Reopen or Clear to reset a phase.", true, c);
                return;
            }

            await RunPlanningPipelineAsync(bookId, skipSb, skipChars, skipThreads, skipChapters, c);
        });

    public Task ContinuePlanningAsync(int bookId, CancellationToken ct = default) =>
        ExecuteAgentRunAsync(bookId, AgentRole.Planner, null, ct, async c =>
        {
            var book = await _repo.GetByIdAsync(bookId)
                ?? throw new InvalidOperationException($"Book {bookId} not found.");

            bool skipSb = book.StoryBibleStatus == PlanningPhaseStatus.Complete;
            bool skipChars = book.CharactersStatus == PlanningPhaseStatus.Complete;
            bool skipThreads = book.PlotThreadsStatus == PlanningPhaseStatus.Complete;
            bool skipChapters = book.ChaptersStatus == PlanningPhaseStatus.Complete;

            if (skipSb && skipChars && skipThreads && skipChapters)
            {
                await _notifier.NotifyWorkflowProgressAsync(bookId,
                    "All planning phases are already complete. Use Reopen or Clear to reset a phase.", true, c);
                return;
            }

            await _notifier.NotifyWorkflowProgressAsync(bookId, "Continuing planning...", false, c);
            await RunPlanningPipelineAsync(bookId, skipSb, skipChars, skipThreads, skipChapters, c);
        });

    public Task StartWritingAsync(int bookId, int chapterId, CancellationToken ct = default) =>
        ExecuteAgentRunAsync(bookId, AgentRole.Writer, chapterId, ct,
            c => _writer.WriteAsync(bookId, chapterId, c));

    public Task StartEditingAsync(int bookId, int chapterId, CancellationToken ct = default) =>
        ExecuteAgentRunAsync(bookId, AgentRole.Editor, chapterId, ct,
            c => _editor.EditAsync(bookId, chapterId, c));

    public Task StartContinuityCheckAsync(int bookId, CancellationToken ct = default) =>
        ExecuteAgentRunAsync(bookId, AgentRole.ContinuityChecker, null, ct,
            async c => await _continuity.CheckAsync(bookId, ct: c));

    /// <summary>
    /// Full autonomous workflow: Plan → Write+Edit each chapter → Continuity check.
    /// Agents may pause to ask the user questions; resume via ResumeWithAnswerAsync.
    /// </summary>
    public Task StartWorkflowAsync(int bookId, CancellationToken ct = default) =>
        ExecuteAgentRunAsync(bookId, AgentRole.Planner, null, ct, async c =>
        {
            await _notifier.NotifyWorkflowProgressAsync(bookId, "Starting workflow…", false, c);

            var book = await _repo.GetByIdAsync(bookId)
                ?? throw new InvalidOperationException($"Book {bookId} not found.");

            bool skipSb      = book.StoryBibleStatus   == PlanningPhaseStatus.Complete;
            bool skipChars   = book.CharactersStatus   == PlanningPhaseStatus.Complete;
            bool skipThreads = book.PlotThreadsStatus  == PlanningPhaseStatus.Complete;
            bool skipChapters = book.ChaptersStatus    == PlanningPhaseStatus.Complete;

            var chapters = await RunPlanningPipelineAsync(bookId, skipSb, skipChars, skipThreads, skipChapters, c);
            await _notifier.NotifyWorkflowProgressAsync(bookId,
                $"Planning complete — {chapters.Count} chapter{(chapters.Count == 1 ? "" : "s")} outlined.", false, c);

            foreach (var chapterRef in chapters.OrderBy(ch => ch.Number))
            {
                c.ThrowIfCancellationRequested();

                // Re-fetch from DB to get true current status (not the planning snapshot)
                var chapter = await _repo.GetChapterAsync(bookId, chapterRef.Id)
                    ?? throw new InvalidOperationException($"Chapter {chapterRef.Id} not found.");

                if (chapter.Status == ChapterStatus.Done)
                {
                    await _notifier.NotifyWorkflowProgressAsync(bookId,
                        $"Chapter {chapter.Number} already done — skipping.", false, c);
                    continue;
                }

                await ProcessChapterAsync(bookId, chapter, c, resumeFromStatus: chapter.Status);
            }

            c.ThrowIfCancellationRequested();
            _state.UpdateRunRole(bookId, AgentRole.ContinuityChecker, null);
            await _notifier.NotifyWorkflowProgressAsync(bookId, "Running final continuity check…", false, c);
            await _continuity.CheckAsync(bookId, ct: c);
            await _notifier.NotifyWorkflowProgressAsync(bookId, "Workflow complete!", true, c);
        });

    public Task StopWorkflowAsync(int bookId)
    {
        _state.CancelRun(bookId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Continue an interrupted workflow: skips Done chapters, resumes writing/editing
    /// from where it left off, then runs the continuity check.
    /// </summary>
    public Task ContinueWorkflowAsync(int bookId, CancellationToken ct = default) =>
        ExecuteAgentRunAsync(bookId, AgentRole.Writer, null, ct, async c =>
        {
            var book = await _repo.GetByIdWithDetailsAsync(bookId)
                ?? throw new InvalidOperationException($"Book {bookId} not found.");

            var allChapters = book.Chapters.OrderBy(ch => ch.Number).ToList();
            if (allChapters.Count == 0)
                throw new InvalidOperationException("No chapters to continue. Run the planner first.");

            await _notifier.NotifyWorkflowProgressAsync(bookId, "Continuing workflow…", false, c);

            foreach (var chapterRef in allChapters)
            {
                c.ThrowIfCancellationRequested();

                // Re-fetch from DB so we see the real current status, not the stale snapshot
                var chapter = await _repo.GetChapterAsync(bookId, chapterRef.Id)
                    ?? throw new InvalidOperationException($"Chapter {chapterRef.Id} not found.");

                if (chapter.Status == ChapterStatus.Done)
                {
                    await _notifier.NotifyWorkflowProgressAsync(bookId,
                        $"Chapter {chapter.Number} already done — skipping.", false, c);
                    continue;
                }

                await ProcessChapterAsync(bookId, chapter, c, resumeFromStatus: chapter.Status);
            }

            c.ThrowIfCancellationRequested();
            _state.UpdateRunRole(bookId, AgentRole.ContinuityChecker, null);
            await _notifier.NotifyWorkflowProgressAsync(bookId, "Running continuity check…", false, c);
            await _continuity.CheckAsync(bookId, ct: c);
            await _notifier.NotifyWorkflowProgressAsync(bookId, "Workflow complete!", true, c);
        });

    // ── Private helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Template for every public agent-run method. Handles the TryStartRun guard,
    /// uniform error/cancellation handling, and status transitions so callers only
    /// need to supply the actual business logic as <paramref name="body"/>.
    /// </summary>
    private async Task ExecuteAgentRunAsync(
        int bookId, AgentRole role, int? chapterId,
        CancellationToken ct, Func<CancellationToken, Task> body)
    {
        var startResult = _state.TryStartRun(bookId, role, chapterId);
        if (startResult == RunStartResult.BookBusy)
            throw new InvalidOperationException("An agent is already running for this book.");
        if (startResult == RunStartResult.AtCapacity)
            throw new InvalidOperationException("Server is at maximum concurrent agent capacity. Please try again when a run completes.");

        // Determine run type from role for recovery service
        var runType = role switch
        {
            AgentRole.Planner or AgentRole.StoryBibleAgent
            or AgentRole.CharactersAgent or AgentRole.PlotThreadsAgent or AgentRole.ChaptersAgent => "plan",
            AgentRole.Writer => "write",
            AgentRole.Editor => "edit",
            AgentRole.ContinuityChecker => "continuity",
            _ => "workflow"
        };

        _logger.LogInformation("[Book {BookId}] {Role} started (chapter={ChapterId}).", bookId, role, chapterId);
        await _state.PersistRunStartAsync(bookId, role, chapterId, runType);
        try
        {
            await body(ct);
            _state.SetStatus(bookId, new AgentRunStatus(role, "Done", chapterId));
            await _state.PersistRunFinishedAsync(bookId, AgentRunPersistStatus.Completed);
            _logger.LogInformation("[Book {BookId}] {Role} finished.", bookId, role);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var cur = _state.GetStatus(bookId);
            var cancelledRole = cur?.Role ?? role;
            _state.SetStatus(bookId, new AgentRunStatus(cancelledRole, "Cancelled", cur?.ChapterId));
            await _state.PersistRunFinishedAsync(bookId, AgentRunPersistStatus.Cancelled);
            await _notifier.NotifyStatusChangedAsync(bookId, cancelledRole, "Cancelled", CancellationToken.None);
            await _notifier.NotifyWorkflowProgressAsync(bookId, "Workflow stopped.", true, CancellationToken.None);
            _logger.LogWarning("[Book {BookId}] {Role} stopped by user.", bookId, role);
            throw;
        }
        catch (OperationCanceledException ex)
        {
            var cur = _state.GetStatus(bookId);
            var failedRole = cur?.Role ?? role;
            _state.SetStatus(bookId, new AgentRunStatus(failedRole, "Failed", cur?.ChapterId));
            await _state.PersistRunFinishedAsync(bookId, AgentRunPersistStatus.Failed);
            await _notifier.NotifyStatusChangedAsync(bookId, failedRole, "Failed", CancellationToken.None);
            await ReportAgentErrorAsync(bookId, failedRole, cur?.ChapterId,
                $"Request cancelled unexpectedly — possible LLM timeout or connection issue. Detail: {ex.Message}");
            await _notifier.NotifyWorkflowProgressAsync(bookId, "Workflow failed (request cancelled).", true, CancellationToken.None);
            _logger.LogError(ex, "[Book {BookId}] {Role} failed due to unexpected cancellation.", bookId, role);
            throw;
        }
        catch (Exception ex)
        {
            var cur = _state.GetStatus(bookId);
            var failedRole = cur?.Role ?? role;
            _state.SetStatus(bookId, new AgentRunStatus(failedRole, "Failed", cur?.ChapterId));
            await _state.PersistRunFinishedAsync(bookId, AgentRunPersistStatus.Failed);
            await _notifier.NotifyStatusChangedAsync(bookId, failedRole, "Failed", CancellationToken.None);
            await _notifier.NotifyWorkflowProgressAsync(bookId, "Workflow failed.", true, CancellationToken.None);
            await ReportAgentErrorAsync(bookId, failedRole, cur?.ChapterId, $"{failedRole} failed: {ex.Message}");
            _logger.LogError(ex, "[Book {BookId}] {Role} failed.", bookId, role);
            throw;
        }
    }

    /// <summary>
    /// Runs the full PreCheck → Write → Checker-Editor loop for one chapter.
    /// The loop runs Checker then optionally human pause, then Editor if issues found.
    /// Loops up to <see cref="MaxEditorIterations"/> times until the chapter is clean.
    /// <paramref name="resumeFromStatus"/> allows ContinueWorkflow to skip the write step.
    /// </summary>
    private const int MaxEditorIterations = 3;

    private async Task ProcessChapterAsync(
        int bookId, Chapter chapter, CancellationToken ct,
        ChapterStatus? resumeFromStatus = null)
    {
        bool needsWrite = resumeFromStatus is null
            || resumeFromStatus == ChapterStatus.Outlined
            || resumeFromStatus == ChapterStatus.Writing
            || string.IsNullOrEmpty(chapter.Content);

        var book = await _repo.GetByIdAsync(bookId)
            ?? throw new InvalidOperationException($"Book {bookId} not found.");

        if (needsWrite)
        {
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

        // Checker-Editor loop (max MaxEditorIterations editor runs)
        int editorRuns = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // Re-read status from DB — resume path may already be Done
            var freshChapter = await _repo.GetChapterAsync(bookId, chapter.Id) ?? chapter;
            if (freshChapter.Status == ChapterStatus.Done) break;
            // Also skip if content is somehow missing
            if (string.IsNullOrEmpty(freshChapter.Content)) break;

            // Run Checker
            _state.UpdateRunRole(bookId, AgentRole.ContinuityChecker, chapter.Id);
            var progressMsg = editorRuns == 0
                ? $"Checking Chapter {chapter.Number}…"
                : $"Re-checking Chapter {chapter.Number} (round {editorRuns + 1})…";
            await _notifier.NotifyWorkflowProgressAsync(bookId, progressMsg, false, ct);
            var checkerResult = await _continuity.CheckAsync(bookId, chapter.Id, ct);
            ct.ThrowIfCancellationRequested();

            // Optional human pause (assisted mode only)
            string humanPoints = string.Empty;
            if (book.HumanAssisted)
            {
                humanPoints = (await _questions.AskSingleOptionalAsync(
                    bookId, chapter.Id, AgentRole.ContinuityChecker,
                    "Want anything to add?", ct)).Trim();
            }

            bool needsEdit = checkerResult.HasIssues || !string.IsNullOrWhiteSpace(humanPoints);

            if (!needsEdit)
            {
                await _notifier.NotifyWorkflowProgressAsync(bookId,
                    $"Chapter {chapter.Number} checks passed.", false, ct);
                break;
            }

            if (editorRuns >= MaxEditorIterations)
            {
                await _repo.AddMessageAsync(new AgentMessage
                {
                    BookId = bookId,
                    ChapterId = chapter.Id,
                    AgentRole = AgentRole.ContinuityChecker,
                    MessageType = MessageType.SystemNote,
                    Content = $"ℹ️ Chapter {chapter.Number}: max editor iterations ({MaxEditorIterations}) reached. Proceeding with remaining issues.",
                    IsResolved = true
                });
                await _notifier.NotifyWorkflowProgressAsync(bookId,
                    $"Chapter {chapter.Number}: max iterations reached, proceeding.", false, ct);
                break;
            }

            // Run Editor
            _state.UpdateRunRole(bookId, AgentRole.Editor, chapter.Id);
            await _notifier.NotifyWorkflowProgressAsync(bookId,
                $"Editing Chapter {chapter.Number}…", false, ct);
            // Don't finalize to Done — loop controls the terminal status
            await _editor.EditAsync(bookId, chapter.Id, ct, checkerResult, humanPoints, finalizeStatus: false);
            editorRuns++;
            ct.ThrowIfCancellationRequested();
        }

        // Ensure chapter is marked Done
        var finalChapter = await _repo.GetChapterAsync(bookId, chapter.Id);
        if (finalChapter is not null && finalChapter.Status != ChapterStatus.Done)
        {
            finalChapter.Status = ChapterStatus.Done;
            await _repo.UpdateChapterAsync(finalChapter);
            await _notifier.NotifyChapterUpdatedAsync(bookId, chapter.Id, ct);
        }

        await _notifier.NotifyWorkflowProgressAsync(bookId,
            $"Chapter {chapter.Number} complete.", false, ct);
    }

    /// <summary>
    /// Runs the 4-phase planning pipeline, optionally skipping phases already marked Complete.
    /// Handles Q&amp;A upfront and delegates each phase to its dedicated agent.
    /// Returns the chapter list from Phase 4, or the existing chapters when Phase 4 is skipped.
    /// </summary>
    private async Task<IReadOnlyList<Chapter>> RunPlanningPipelineAsync(
        int bookId,
        bool skipStoryBible,
        bool skipCharacters,
        bool skipPlotThreads,
        bool skipChapters,
        CancellationToken ct)
    {
        var book = await _repo.GetByIdWithDetailsAsync(bookId)
            ?? throw new InvalidOperationException($"Book {bookId} not found.");

        await _notifier.NotifyStatusChangedAsync(bookId, AgentRole.Planner, "Running", ct);

        bool isContinuation = skipStoryBible || skipCharacters || skipPlotThreads || skipChapters;

        // Continuation: reload prior Q&A context. Fresh run: gather and ask upfront questions.
        var qaContext = isContinuation
            ? await _questions.LoadExistingContextAsync(bookId)
            : new System.Text.StringBuilder();

        if (!isContinuation)
        {
            var bkCtx = $"Title: {book.Title}\nGenre: {book.Genre}\nPremise: {book.Premise}\nTarget chapters: {book.TargetChapterCount}";
            var questions = await _questions.GatherQuestionsAsync(bookId, bkCtx, book.Language, ct);
            if (questions.Count > 0)
                await _questions.AskQuestionsAsync(bookId, questions, qaContext, ct);
        }

        var qaStr = qaContext.ToString();

        // Phase 1: Story Bible
        StoryBible bible;
        if (skipStoryBible)
        {
            await _notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Skipping Story Bible (marked complete).", false, ct);
            bible = await _repo.GetStoryBibleAsync(bookId)
                ?? throw new InvalidOperationException("Story Bible is marked complete but was not found in DB.");
        }
        else
        {
            bible = await _storyBibleAgent.RunAsync(book, qaStr, ct);

            if (book.HumanAssisted)
            {
                var note = (await _questions.AskSingleOptionalAsync(
                    bookId, null, AgentRole.StoryBibleAgent,
                    "Story Bible is complete. Want anything to add before moving to Characters?", ct)).Trim();
                if (!string.IsNullOrEmpty(note))
                {
                    qaContext.AppendLine($"\n[User note after Story Bible]: {note}");
                    qaStr = qaContext.ToString();
                }
            }
        }

        // Phase 2: Character Cards
        IReadOnlyList<CharacterCard> characters;
        if (skipCharacters)
        {
            await _notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Skipping Characters (marked complete).", false, ct);
            characters = (await _repo.GetCharacterCardsAsync(bookId)).ToList();
        }
        else
        {
            characters = await _charactersAgent.RunAsync(book, bible, qaStr, ct);

            if (book.HumanAssisted)
            {
                var note = (await _questions.AskSingleOptionalAsync(
                    bookId, null, AgentRole.CharactersAgent,
                    "Characters are complete. Want anything to add before moving to Plot Threads?", ct)).Trim();
                if (!string.IsNullOrEmpty(note))
                {
                    qaContext.AppendLine($"\n[User note after Characters]: {note}");
                    qaStr = qaContext.ToString();
                }
            }
        }

        // Phase 3: Plot Threads
        IReadOnlyList<PlotThread> threads;
        if (skipPlotThreads)
        {
            await _notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Skipping Plot Threads (marked complete).", false, ct);
            threads = (await _repo.GetPlotThreadsAsync(bookId)).ToList();
        }
        else
        {
            threads = await _plotThreadsAgent.RunAsync(book, bible, characters, qaStr, ct);

            if (book.HumanAssisted)
            {
                var note = (await _questions.AskSingleOptionalAsync(
                    bookId, null, AgentRole.PlotThreadsAgent,
                    "Plot Threads are complete. Want anything to add before generating Chapter Outlines?", ct)).Trim();
                if (!string.IsNullOrEmpty(note))
                {
                    qaContext.AppendLine($"\n[User note after Plot Threads]: {note}");
                    qaStr = qaContext.ToString();
                }
            }
        }

        // Phase 4: Chapter Outlines
        IReadOnlyList<Chapter> chapters;
        if (skipChapters)
        {
            await _notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Skipping Chapter Outlines (marked complete).", false, ct);
            chapters = (await _repo.GetChaptersAsync(bookId)).ToList();
        }
        else
        {
            chapters = await _planner.RunAsync(book, bible, characters, threads, qaStr, ct);

            if (book.HumanAssisted)
            {
                var note = (await _questions.AskSingleOptionalAsync(
                    bookId, null, AgentRole.Planner,
                    "Chapter Outlines are complete. Any final notes before writing begins?", ct)).Trim();
                if (!string.IsNullOrEmpty(note))
                {
                    qaContext.AppendLine($"\n[User note after Chapter Outlines]: {note}");
                    // qaStr only used downstream if StartWorkflowAsync passes it; currently unused after this point
                }
            }
        }

        await _notifier.NotifyStatusChangedAsync(bookId, AgentRole.Planner, "Done", ct);
        return chapters;
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
        var message = await _repo.FindMessageByIdAsync(messageId);
        if (message is null) return;

        // Idempotency: if already resolved, do nothing
        if (message.IsResolved) return;

        // Mark message resolved in DB
        message.IsResolved = true;
        await _repo.UpdateMessageAsync(message);

        // Persist answer as a new message
        await _repo.AddMessageAsync(new AgentMessage
        {
            BookId = message.BookId,
            ChapterId = message.ChapterId,
            AgentRole = message.AgentRole,
            MessageType = MessageType.Answer,
            Content = answer,
            IsResolved = true
        });

        // Unblock the waiting agent (in-process path — works normally or after rehydration)
        var resolved = _state.TryResolvePending(message.BookId, messageId, answer);
        if (!resolved)
        {
            _logger.LogWarning("[Book {BookId}] No in-memory pending TCS for message {MessageId} — run may have been rehydrated or answer was already handled.", message.BookId, messageId);
        }
    }

    public Task<AgentRunStatus?> GetRunStatusAsync(int bookId) =>
        Task.FromResult(_state.GetStatus(bookId));
}
