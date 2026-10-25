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
    private readonly IllustrationPlannerAgent _illustrationPlanner;
    private readonly IllustrationRenderer _illustrationRenderer;
    private readonly AgentRunStateService _state;
    private readonly IBookNotifier _notifier;
    private readonly BookChatAgent _bookChat;
    private readonly ProposalExecutor _proposals;
    private readonly IBookRewinder _rewinder;
    private readonly IChapterIndexQueue _indexQueue;
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
        IllustrationPlannerAgent illustrationPlanner,
        IllustrationRenderer illustrationRenderer,
        AgentRunStateService state,
        IBookNotifier notifier,
        BookChatAgent bookChat,
        ProposalExecutor proposals,
        IBookRewinder rewinder,
        IChapterIndexQueue indexQueue,
        ILogger<AgentOrchestrator> logger)
    {
        _rewinder = rewinder;
        _indexQueue = indexQueue;
        _bookChat = bookChat;
        _proposals = proposals;
        _repo = repo;
        _questions = questions;
        _storyBibleAgent = storyBibleAgent;
        _charactersAgent = charactersAgent;
        _plotThreadsAgent = plotThreadsAgent;
        _planner = planner;
        _writer = writer;
        _editor = editor;
        _continuity = continuity;
        _illustrationPlanner = illustrationPlanner;
        _illustrationRenderer = illustrationRenderer;
        _state = state;
        _notifier = notifier;
        _logger = logger;
    }

    public Task RewindAsync(int bookId, DateTime at, string? fingerprint, string? origin, CancellationToken ct = default) =>
        ExecuteAgentRunAsync(bookId, AgentRole.Rewind, null, ct, async c =>
        {
            await _notifier.NotifyWorkflowProgressAsync(bookId, $"Rewinding to {at:yyyy-MM-dd HH:mm:ss} UTC…", false, c);
            var result = await _rewinder.RewindAsync(bookId, at, fingerprint, origin, c);

            // Restored text that was never embedded before: RAG needs it for the next step.
            foreach (var (chapterId, versionId) in result.VersionsToIndex)
                _indexQueue.QueueIndex(bookId, chapterId, versionId);

            var plan = result.Plan;
            await _repo.AddMessageAsync(new AgentMessage
            {
                BookId = bookId,
                AgentRole = AgentRole.Rewind,
                MessageType = MessageType.SystemNote,
                Content = $"⏪ Rewound the book to {at:yyyy-MM-dd HH:mm:ss} UTC — {plan.Changes.Count} change(s)" +
                          (plan.HiddenChatMessages > 0 ? $", {plan.HiddenChatMessages} book-chat message(s) hidden" : "") +
                          ". Everything in between stays in each part's history." +
                          (plan.Changes.Count > 0 ? "\n\n" + string.Join("\n", plan.Changes.Select(ch => $"- {ch.Kind}: {ch.Label}" +
                              (ch.Fields.Count > 0 ? $" ({string.Join(", ", ch.Fields)})" : ""))) : ""),
                IsResolved = true,
                Origin = origin,
            });
            foreach (var chapterId in result.ChangedChapterIds)
                await NotifySafelyAsync(bookId, () => _notifier.NotifyChapterUpdatedAsync(bookId, chapterId, CancellationToken.None));
            await NotifySafelyAsync(bookId, () => _notifier.NotifyMessagesUpdatedAsync(bookId, CancellationToken.None));
            await NotifySafelyAsync(bookId, () => _notifier.NotifyBookChatAsync(bookId, null, CancellationToken.None));
            await NotifySafelyAsync(bookId, () => _notifier.NotifyRewoundAsync(bookId, at, result.HiddenChatMessageIds, origin, CancellationToken.None));
        }, runType: "rewind");

    public Task StartPlanningAsync(int bookId, CancellationToken ct = default) =>
        RunPlanningAsync(bookId, isContinue: false, ct);

    public Task ContinuePlanningAsync(int bookId, CancellationToken ct = default) =>
        RunPlanningAsync(bookId, isContinue: true, ct);

    private Task RunPlanningAsync(int bookId, bool isContinue, CancellationToken ct) =>
        ExecuteAgentRunAsync(bookId, AgentRole.Planner, null, ct, async c =>
        {
            var book = await _repo.GetByIdAsync(bookId)
                ?? throw new InvalidOperationException($"Book {bookId} not found.");

            var (skipSb, skipChars, skipThreads, skipChapters) = GetPlanningSkipFlags(book);

            if (skipSb && skipChars && skipThreads && skipChapters)
            {
                await _notifier.NotifyWorkflowProgressAsync(bookId,
                    "All planning phases are already complete. Use Reopen or Clear to reset a phase.", true, c);
                return;
            }

            if (isContinue)
                await _notifier.NotifyWorkflowProgressAsync(bookId, "Continuing planning...", false, c);

            await RunPlanningPipelineAsync(bookId, skipSb, skipChars, skipThreads, skipChapters, c);
        });

    /// <summary>Divides a section into chapters (one LLM call); <paramref name="chapterCount"/> null lets the model decide.</summary>
    public Task StartSectionBreakdownAsync(int bookId, int sectionId, int? chapterCount, CancellationToken ct = default) =>
        ExecuteAgentRunAsync(bookId, AgentRole.ChaptersAgent, null, ct,
            c => _planner.BreakDownSectionAsync(bookId, sectionId, chapterCount, c), runType: "breakdown");

    /// <summary>
    /// Autonomous runs: every section the planner expects to need several chapters, and that still has a single
    /// unwritten chapter, is broken down into that many chapters before writing starts.
    /// </summary>
    private async Task BreakDownPlannedSectionsAsync(int bookId, CancellationToken ct)
    {
        var chapters = (await _repo.GetChaptersAsync(bookId)).ToList();
        foreach (var section in await _repo.GetSectionsAsync(bookId))
        {
            ct.ThrowIfCancellationRequested();
            if (section.PlannedChapters <= 1) continue;
            var own = chapters.Where(ch => ch.SectionId == section.Id).ToList();
            if (own.Count != 1 || !string.IsNullOrWhiteSpace(own[0].Content)) continue;
            await _planner.BreakDownSectionAsync(bookId, section.Id, section.PlannedChapters, ct);
        }
    }

    public Task StartWritingAsync(int bookId, int chapterId, CancellationToken ct = default) =>
        ExecuteAgentRunAsync(bookId, AgentRole.Writer, chapterId, ct,
            c => _writer.WriteAsync(bookId, chapterId, c));

    public Task StartEditingAsync(int bookId, int chapterId, CancellationToken ct = default) =>
        ExecuteAgentRunAsync(bookId, AgentRole.Editor, chapterId, ct,
            c => _editor.EditAsync(bookId, chapterId, c));

    public Task StartContinuityCheckAsync(int bookId, int? chapterId = null, CancellationToken ct = default) =>
        ExecuteAgentRunAsync(bookId, AgentRole.ContinuityChecker, chapterId, ct,
            async c => await _continuity.CheckAsync(bookId, chapterId, c));

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

            var (skipSb, skipChars, skipThreads, skipChapters) = GetPlanningSkipFlags(book);

            var chapters = await RunPlanningPipelineAsync(bookId, skipSb, skipChars, skipThreads, skipChapters, c);
            await _notifier.NotifyWorkflowProgressAsync(bookId,
                $"Planning complete — {chapters.Count} chapter{(chapters.Count == 1 ? "" : "s")} outlined.", false, c);

            await BreakDownPlannedSectionsAsync(bookId, c);

            // Reload: the breakdown may have added chapters.
            foreach (var chapter in (await _repo.GetChaptersAsync(bookId)).ToList())
            {
                c.ThrowIfCancellationRequested();

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
            await PlanCoverIfEnabledAsync(bookId, c);
            // Terminal WorkflowProgress(isComplete=true) is emitted by ExecuteAgentRunAsync after this body returns.
        });

    /// <summary>
    /// Continue an interrupted workflow: skips Done chapters, resumes writing/editing
    /// from where it left off, then runs the continuity check.
    /// </summary>
    public Task ContinueWorkflowAsync(int bookId, CancellationToken ct = default) =>
        ExecuteAgentRunAsync(bookId, AgentRole.Writer, null, ct, async c =>
        {
            var book = await _repo.GetByIdAsync(bookId)
                ?? throw new InvalidOperationException($"Book {bookId} not found.");

            if (!(await _repo.GetChaptersAsync(bookId)).Any())
                throw new InvalidOperationException("No chapters to continue. Run the planner first.");

            await _notifier.NotifyWorkflowProgressAsync(bookId, "Continuing workflow…", false, c);
            await BreakDownPlannedSectionsAsync(bookId, c);
            // Loaded after the breakdown, which may have added chapters.
            var allChapters = (await _repo.GetChaptersAsync(bookId)).ToList();

            foreach (var chapter in allChapters)
            {
                c.ThrowIfCancellationRequested();

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
            await PlanCoverIfEnabledAsync(bookId, c);
            // Terminal WorkflowProgress(isComplete=true) is emitted by ExecuteAgentRunAsync after this body returns.
        });

    public Task StartIllustrationPlanningAsync(int bookId, IllustrationPlanRequest request, CancellationToken ct = default) =>
        ExecuteAgentRunAsync(bookId, AgentRole.IllustrationPlanner, request.Scope == IllustrationPlanScope.Chapter ? request.TargetId : null, ct,
            async c =>
            {
                switch (request.Scope)
                {
                    case IllustrationPlanScope.Chapter:
                        await _illustrationPlanner.PlanChapterAsync(bookId,
                            request.TargetId ?? throw new ArgumentException("A chapter id is required."), c);
                        break;
                    case IllustrationPlanScope.Cover:
                        await _illustrationPlanner.PlanCoverAsync(bookId, c);
                        break;
                    case IllustrationPlanScope.Style:
                        await _illustrationPlanner.EnsureStyleSheetAsync(bookId, c, force: true);
                        break;
                    case IllustrationPlanScope.Characters:
                        await _illustrationPlanner.EnsureCharacterVisualsAsync(bookId, c, force: true);
                        break;
                    default:
                        await _illustrationPlanner.PlanMissingAsync(bookId, c);
                        break;
                }
            });

    public Task StartRenderAsync(int bookId, RenderRequest request, CancellationToken ct = default) =>
        ExecuteAgentRunAsync(bookId, AgentRole.Illustrator, null, ct,
            c => _illustrationRenderer.RenderAsync(bookId, request, c));

    // ── Book chat (Interactive books) ───────────────────────────────────────────

    public Task RunBookChatTurnAsync(int bookId, int? authorMessageId, CancellationToken ct = default) =>
        ExecuteAgentRunAsync(bookId, AgentRole.BookChat, null, ct,
            async c =>
            {
                await _bookChat.ReplyAsync(bookId, authorMessageId, c);
                // A queued chapter write may have been cleared to go meanwhile (a suggestion rejected during the reply).
                await _proposals.ApplyAsync(bookId, [], c);
            }, runType: "chat");

    public Task ApplyProposalsAsync(int bookId, IReadOnlyList<int> proposalIds, CancellationToken ct = default) =>
        ExecuteAgentRunAsync(bookId, AgentRole.BookChat, null, ct,
            c => _proposals.ApplyAsync(bookId, proposalIds, c), runType: "chat-apply");

    public Task UndoProposalAsync(int bookId, int proposalId, CancellationToken ct = default) =>
        ExecuteAgentRunAsync(bookId, AgentRole.BookChat, null, ct,
            async c =>
            {
                await _proposals.UndoAsync(bookId, proposalId, c);
                await _proposals.ApplyAsync(bookId, [], c);   // queued chapter writes waiting on what was undone
            }, runType: "chat-undo");

    // ── Private helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Plans the cover once the whole book is written, when the book has illustrations enabled.
    /// A failure is reported as a note: the book itself is finished.
    /// </summary>
    private async Task PlanCoverIfEnabledAsync(int bookId, CancellationToken ct)
    {
        var book = await _repo.GetByIdAsync(bookId);
        if (book is not { IllustrationsEnabled: true }) return;
        _state.UpdateRunRole(bookId, AgentRole.IllustrationPlanner, null);
        await _illustrationPlanner.PlanCoverOrNoteAsync(bookId, ct);
    }

    /// <summary>
    /// Template for every public agent-run method: uniform error/cancellation handling and status
    /// transitions, so callers only supply the business logic as <paramref name="body"/>.
    /// The book's slot must already be claimed via <c>AgentRunnerService.TryStart</c>
    /// (<see cref="AgentRunStateService.TryStartRun"/>), which also owns <paramref name="ct"/>.
    /// </summary>
    private async Task ExecuteAgentRunAsync(
        int bookId, AgentRole role, int? chapterId,
        CancellationToken ct, Func<CancellationToken, Task> body, string? runType = null)
    {
        if (_state.GetStatus(bookId) is not { IsActive: true })
            throw new InvalidOperationException(
                $"Run for book {bookId} was not started through AgentRunnerService.TryStart.");

        // Determine run type from role for recovery service
        runType ??= role switch
        {
            AgentRole.Planner or AgentRole.StoryBibleAgent
            or AgentRole.CharactersAgent or AgentRole.PlotThreadsAgent or AgentRole.ChaptersAgent => "plan",
            AgentRole.Writer => "write",
            AgentRole.Editor => "edit",
            AgentRole.ContinuityChecker => "continuity",
            AgentRole.IllustrationPlanner => "illustrate-plan",
            AgentRole.Illustrator => "illustrate",
            _ => "workflow"
        };

        _logger.LogInformation("[Book {BookId}] {Role} started (chapter={ChapterId}).", bookId, role, chapterId);
        await _state.PersistRunStartAsync(bookId, role, chapterId, runType);
        try
        {
            await body(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var cur = _state.GetStatus(bookId);
            var cancelledRole = cur?.Role ?? role;
            _state.SetStatus(bookId, new AgentRunStatus(cancelledRole, RunStates.Cancelled, cur?.ChapterId));
            await _state.PersistRunFinishedAsync(bookId, AgentRunPersistStatus.Cancelled);
            await NotifySafelyAsync(bookId, () => _notifier.NotifyStatusChangedAsync(bookId, cancelledRole, RunStates.Cancelled, cur?.ChapterId, CancellationToken.None));
            await NotifySafelyAsync(bookId, () => _notifier.NotifyWorkflowProgressAsync(bookId, "Workflow stopped.", true, CancellationToken.None));
            _logger.LogWarning("[Book {BookId}] {Role} stopped by user.", bookId, role);
            throw;
        }
        catch (OperationCanceledException ex)
        {
            await FailRunAsync(bookId, role, ex,
                $"Request cancelled unexpectedly — possible LLM timeout or connection issue. Detail: {ex.Message}",
                "Workflow failed (request cancelled).");
            throw;
        }
        catch (Exception ex)
        {
            var failedRole = _state.GetStatus(bookId)?.Role ?? role;
            await FailRunAsync(bookId, role, ex, $"{failedRole} failed: {ex.Message}", "Workflow failed.");
            throw;
        }

        // Success. Kept outside the try so a notification failure can never turn finished work into "Failed".
        _state.SetStatus(bookId, new AgentRunStatus(role, RunStates.Done, chapterId));
        await _state.PersistRunFinishedAsync(bookId, AgentRunPersistStatus.Completed);
        // Emit the unambiguous "run is fully done" terminal signal so the UI can reliably
        // clear the running state regardless of which intermediate "Done" events were fired.
        var completionMsg = runType switch
        {
            "write"      => "Writing complete.",
            "edit"       => "Editing complete.",
            "continuity" => "Continuity check complete.",
            "plan"       => "Planning complete.",
            "illustrate-plan" => "Illustration planning complete.",
            "illustrate" => "Illustrating complete.",
            "chat"       => "Reply ready.",
            "chat-apply" => "Changes applied.",
            "chat-undo"  => "Change undone.",
            "rewind"     => "Rewind complete.",
            _            => "Workflow complete!",
        };
        await NotifySafelyAsync(bookId, () => _notifier.NotifyWorkflowProgressAsync(bookId, completionMsg, true, CancellationToken.None));
        _logger.LogInformation("[Book {BookId}] {Role} finished.", bookId, role);
    }

    /// <summary>Moves the run to Failed, records the error in the chat and tells the UI the run is over.</summary>
    private async Task FailRunAsync(int bookId, AgentRole role, Exception ex, string errorMessage, string progressMessage)
    {
        var cur = _state.GetStatus(bookId);
        var failedRole = cur?.Role ?? role;
        _state.SetStatus(bookId, new AgentRunStatus(failedRole, RunStates.Failed, cur?.ChapterId));
        await _state.PersistRunFinishedAsync(bookId, AgentRunPersistStatus.Failed);
        await NotifySafelyAsync(bookId, () => _notifier.NotifyStatusChangedAsync(bookId, failedRole, RunStates.Failed, cur?.ChapterId, CancellationToken.None));
        await ReportAgentErrorAsync(bookId, failedRole, cur?.ChapterId, errorMessage);
        await NotifySafelyAsync(bookId, () => _notifier.NotifyWorkflowProgressAsync(bookId, progressMessage, true, CancellationToken.None));
        _logger.LogError(ex, "[Book {BookId}] {Role} failed.", bookId, role);
    }

    /// <summary>Runs a best-effort UI notification: a failure is logged, never allowed to break the run's bookkeeping.</summary>
    private async Task NotifySafelyAsync(int bookId, Func<Task> notify)
    {
        try { await notify(); }
        catch (Exception ex) { _logger.LogWarning(ex, "[Book {BookId}] UI notification failed.", bookId); }
    }

    /// <summary>
    /// Runs the fixed sequence for one chapter: PreCheck → Write → Check → Edit (if issues) → Check again → Done.
    /// <paramref name="resumeFromStatus"/> allows ContinueWorkflow to skip the write step.
    /// </summary>
    private async Task ProcessChapterAsync(
        int bookId, Chapter chapter, CancellationToken ct,
        ChapterStatus? resumeFromStatus = null)
    {
        bool needsWrite = resumeFromStatus is null
            || resumeFromStatus == ChapterStatus.Outlined
            || resumeFromStatus == ChapterStatus.Writing
            || string.IsNullOrEmpty(chapter.Content);

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

        // Re-read from DB — resume path may already be Done
        var freshChapter = await _repo.GetChapterAsync(bookId, chapter.Id);
        if (freshChapter is null)
        {
            _logger.LogWarning("[Book {BookId}] Chapter {ChapterId} not found in DB after write/check phase — using cached version.", bookId, chapter.Id);
            freshChapter = chapter;
        }
        if (freshChapter.Status != ChapterStatus.Done && !string.IsNullOrEmpty(freshChapter.Content))
        {
            // Quality check — produces verbatim patches plus any issues needing a creative rewrite
            _state.UpdateRunRole(bookId, AgentRole.ContinuityChecker, chapter.Id);
            await _notifier.NotifyWorkflowProgressAsync(bookId,
                $"Checking Chapter {chapter.Number}…", false, ct);
            var checkerResult = await _continuity.CheckAsync(bookId, chapter.Id, ct);
            ct.ThrowIfCancellationRequested();

            var patches = checkerResult.HasIssues ? EditorAgent.SelectPatches(checkerResult) : [];
            var rewrites = checkerResult.HasIssues ? EditorAgent.SelectRewrites(checkerResult) : [];

            // Mechanical patches first — deterministic, no LLM call
            if (patches.Length > 0)
            {
                _state.UpdateRunRole(bookId, AgentRole.Editor, chapter.Id);
                await _notifier.NotifyWorkflowProgressAsync(bookId,
                    $"Applying {patches.Length} mechanical fix(es) to Chapter {chapter.Number}…", false, ct);
                await _editor.ApplyMechanicalFixesAsync(bookId, chapter.Id,
                    new CheckerResult(true, patches, checkerResult.Summary), finalizeStatus: false, ct);
                ct.ThrowIfCancellationRequested();
            }
            else if (!checkerResult.HasIssues)
            {
                await _notifier.NotifyWorkflowProgressAsync(bookId,
                    $"Chapter {chapter.Number} checks passed.", false, ct);
            }

            // The creative pass costs a full-chapter LLM call — only run it when the checker asked for one.
            if (rewrites.Count > 0)
            {
                _state.UpdateRunRole(bookId, AgentRole.Editor, chapter.Id);
                await _notifier.NotifyWorkflowProgressAsync(bookId,
                    $"Creative rewrite for Chapter {chapter.Number}…", false, ct);
                await _editor.RewriteAsync(bookId, chapter.Id, rewrites, authorNotes: null, finalizeStatus: false, ct);
                ct.ThrowIfCancellationRequested();
            }
            // No final check — patches are deterministic; any failures reported in the chat panel
        }
        else if (freshChapter.Status == ChapterStatus.Done)
        {
            // Chapter was already completed (e.g. from a previous partial run or manual intervention).
            _logger.LogInformation("[Book {BookId}] Skipping Chapter {ChapterNumber} — already Done.", bookId, chapter.Number);
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

        // Prompts and positions only — rendering is a separate run, so the text model can be unloaded first.
        // Read fresh so the toggle can be switched while the workflow runs.
        if ((await _repo.GetByIdAsync(bookId))?.IllustrationsEnabled == true)
        {
            _state.UpdateRunRole(bookId, AgentRole.IllustrationPlanner, chapter.Id);
            await _illustrationPlanner.PlanChapterOrNoteAsync(bookId, chapter, ct);
        }
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
        var book = await ReloadBookAsync(bookId);

        _state.UpdateRunRole(bookId, AgentRole.Planner, null);
        await _notifier.NotifyStatusChangedAsync(bookId, AgentRole.Planner, RunStates.Running, ct: ct);

        // Clarifications: asked once. The questions are saved open and answered on the Clarifications
        // page, so an interrupted round loses nothing — a re-run waits for the questions still open
        // instead of asking new ones.
        if (book.ClarificationsStatus == PlanningPhaseStatus.Complete)
        {
            await _notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Skipping clarifications (marked complete).", false, ct);
        }
        else
        {
            bool roundComplete;
            if ((await _repo.GetClarificationsAsync(bookId)).Any(c => c.IsOpen))
            {
                roundComplete = await _questions.WaitForAnswersAsync(bookId, ct);
            }
            else
            {
                var ancestorRef = PromptTemplates.AncestorReference(await _repo.GetAncestorPlanningContextAsync(bookId, ct));
                var bkCtx = PromptTemplates.QuestionGatheringUser(book, ancestorRef);
                var questions = await _questions.GatherQuestionsAsync(bookId, bkCtx, book.Language, ct);
                roundComplete = questions.Count == 0 || await _questions.AskQuestionsAsync(bookId, questions, ct);
            }

            // Reset by the author while waiting (Archive all): plan without, and ask again next run.
            book = await ReloadBookAsync(bookId);
            book.ClarificationsStatus = roundComplete ? PlanningPhaseStatus.Complete : PlanningPhaseStatus.NotStarted;
            await _repo.UpdateAsync(book);
        }

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
            bible = await _storyBibleAgent.RunAsync(book, ct);
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
            characters = await _charactersAgent.RunAsync(book, bible, ct);
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
            threads = await _plotThreadsAgent.RunAsync(book, bible, characters, ct);
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
            chapters = await _planner.RunAsync(book, bible, characters, threads, ct);
        }

        await _notifier.NotifyStatusChangedAsync(bookId, AgentRole.Planner, RunStates.Done, ct: ct);
        return chapters;
    }

    /// <summary>The book with its active chapters and clarifications, freshly read.</summary>
    private async Task<Book> ReloadBookAsync(int bookId) =>
        await _repo.GetByIdWithActiveDetailsAsync(bookId)
            ?? throw new InvalidOperationException($"Book {bookId} not found.");

    private static (bool skipSb, bool skipChars, bool skipThreads, bool skipChapters) GetPlanningSkipFlags(Book book) =>
        (book.StoryBibleStatus  == PlanningPhaseStatus.Complete,
         book.CharactersStatus  == PlanningPhaseStatus.Complete,
         book.PlotThreadsStatus == PlanningPhaseStatus.Complete,
         book.ChaptersStatus    == PlanningPhaseStatus.Complete);

    private Task ReportAgentErrorAsync(int bookId, AgentRole role, int? chapterId, string message) =>
        AgentBase.ReportAgentErrorCoreAsync(_repo, _notifier, _logger, bookId, role, chapterId, message);

    public async Task ResumeWithAnswerAsync(int messageId, string answer, CancellationToken ct = default, string? origin = null)
    {
        var message = await _repo.FindMessageByIdAsync(messageId);
        if (message is null)
        {
            _logger.LogWarning("[Book ?] ResumeWithAnswerAsync: no message found for messageId={MessageId}. Answer silently dropped.", messageId);
            return;
        }

        // Idempotency: if already resolved, do nothing
        if (message.IsResolved)
        {
            _logger.LogWarning("[Book {BookId}] ResumeWithAnswerAsync: message {MessageId} already resolved. Answer silently dropped.",
                message.BookId, messageId);
            return;
        }

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
            IsResolved = true,
            QuestionMessageId = message.Id,
            Origin = origin,
        });

        // Other open tabs/devices still show the question box until they re-read the messages.
        await NotifySafelyAsync(message.BookId, () => _notifier.NotifyMessagesUpdatedAsync(message.BookId, CancellationToken.None));

        // Unblock the waiting agent (in-process path — works normally or after rehydration)
        var resolved = _state.TryResolvePending(message.BookId, messageId, answer);
        if (!resolved)
        {
            _logger.LogWarning("[Book {BookId}] No in-memory pending TCS for message {MessageId} — run may have been rehydrated or answer was already handled.", message.BookId, messageId);
        }
    }
}
