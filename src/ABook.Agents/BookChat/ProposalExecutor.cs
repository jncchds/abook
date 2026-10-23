using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.Logging;

namespace ABook.Agents;

/// <summary>
/// Applies the proposals the author approved in the book chat, and undoes applied ones. Quick changes (bible,
/// characters, threads, outlines, notes) run first, in reply order; prose changes (write, revise, patch) run
/// after them, and each written or revised chapter is then summarized and checked, with what the check finds
/// posted back to the chat as new proposals.
/// </summary>
public class ProposalExecutor(
    IBookRepository repo,
    IBookNotifier notifier,
    AgentRunStateService state,
    BookContentService content,
    BookChatAgent chat,
    WriterAgent writer,
    EditorAgent editor,
    ContinuityCheckerAgent checker,
    ChapterSummarizer summarizer,
    IllustrationPlannerAgent illustrationPlanner,
    ILogger<ProposalExecutor> logger)
{
    /// <summary>Types whose preview shows the target as it read when proposed — applying after it changed needs confirming.</summary>
    private static readonly HashSet<string> StaleChecked =
    [
        BookChatActions.UpdatePremise, BookChatActions.UpdateStoryBible,
        BookChatActions.UpsertCharacter, BookChatActions.ArchiveCharacter,
        BookChatActions.UpsertPlotThread, BookChatActions.ArchivePlotThread,
        BookChatActions.UpdateChapterOutline, BookChatActions.ArchiveChapter,
    ];

    /// <summary>The proposals whose target changed since they were proposed: applying them would overwrite that change.</summary>
    public async Task<IReadOnlyList<ChatProposal>> FindStaleAsync(int bookId, IReadOnlyList<ChatProposal> proposals)
    {
        var stale = new List<ChatProposal>();
        foreach (var p in proposals.Where(p => StaleChecked.Contains(p.Type)))
            if (await content.RenderAsync(bookId, p.Target) != p.PreviewBefore) stale.Add(p);
        return stale;
    }

    /// <summary>Why <paramref name="p"/> cannot be undone now, or null when it can.</summary>
    public async Task<string?> UndoBlockerAsync(int bookId, ChatProposal p)
    {
        if (p.Status != ChatProposalStatus.Applied) return "Only an applied change can be undone.";
        if (p.Type is BookChatActions.UpdatePremise or BookChatActions.UpdateStoryBible
            && await repo.GetByIdAsync(bookId) is { } book && BookContentService.IsBibleLocked(book))
            return "The premise and Story Bible are locked. Unlock them in Book Settings first.";
        var applied = await repo.GetProposalsByStatusAsync(bookId, ChatProposalStatus.Applied);
        var later = applied.FirstOrDefault(o => o.Id != p.Id && o.Target == p.Target && p.Target is not null && o.AppliedAt > p.AppliedAt);
        if (later is not null) return $"\"{later.Label}\" changed the same thing later — undo it first.";
        var dependent = applied.FirstOrDefault(o => o.DependsOnProposalId == p.Id);
        if (dependent is not null) return $"\"{dependent.Label}\" builds on this change — undo it first.";
        // Undoing an added chapter removes it: not while it holds prose (written in the chat or by hand).
        if (p.Type == BookChatActions.AddChapter && p.Target is { } t && t.StartsWith("chapter:")
            && int.TryParse(t[8..], out var chapterId)
            && await repo.GetChapterAsync(bookId, chapterId) is { IsArchived: false } chapter && chapter.Content.Trim().Length > 0)
            return $"Chapter {chapter.Number} has been written — undo its writing first.";
        return null;
    }

    /// <summary>True when the target no longer reads as the change left it (edited since): undo would discard that edit.</summary>
    public async Task<bool> IsUndoStaleAsync(int bookId, ChatProposal p) =>
        p.Target is not null && p.AfterJson is not null && await content.RenderAsync(bookId, p.Target) != p.AfterJson;

    // ── Apply ───────────────────────────────────────────────────────────────────

    public async Task ApplyAsync(int bookId, IReadOnlyList<int> proposalIds, CancellationToken ct)
    {
        var proposals = (await repo.GetProposalsAsync(bookId, proposalIds))
            .Where(p => p.Status is ChatProposalStatus.Pending or ChatProposalStatus.Failed)
            .ToList();
        if (proposals.Count == 0) return;

        await RejectOtherOptionsAsync(bookId, proposals);

        var quick = proposals.Where(p => !BookChatActions.ProseActions.Contains(p.Type)).ToList();
        var prose = proposals.Where(p => BookChatActions.ProseActions.Contains(p.Type)).ToList();
        foreach (var p in quick) await ApplyOneAsync(bookId, p, ct);

        // Writing the first chapter moves the book to its writing stage.
        if (prose.Any(p => p.Type == BookChatActions.WriteChapter)
            && await repo.GetByIdAsync(bookId) is { Mode: BookMode.Interactive, StoryBibleLockedAt: null }
            && await content.LockStoryBibleAsync(bookId))
            await chat.AddEventAsync(bookId, "📌 The premise and Story Bible are now locked — writing has started.");

        // Chapter → whether it needs a continuity check (written or revised; a patch alone only needs a new summary).
        var changed = new Dictionary<int, bool>();
        foreach (var p in prose)
            if (await ApplyOneAsync(bookId, p, ct) is int chapterId)
                changed[chapterId] = changed.GetValueOrDefault(chapterId) || p.Type != BookChatActions.PatchChapter;

        foreach (var (chapterId, check) in changed)
            await FollowUpAsync(bookId, chapterId, check, ct);

        await OfferNextStepsAsync(bookId, quick, prose);
    }

    /// <summary>
    /// The obvious next step after an outline is settled, offered as a card without asking the chat model: write a
    /// chapter whose outline was added or changed and that has no prose yet, or revise a written one to its new
    /// outline. Skipped when this batch already changed that chapter's prose or a card for it is still open.
    /// </summary>
    private async Task OfferNextStepsAsync(int bookId, IReadOnlyList<ChatProposal> quick, IReadOnlyList<ChatProposal> prose)
    {
        var outlined = quick
            .Where(p => p.Status == ChatProposalStatus.Applied
                        && p.Type is BookChatActions.AddChapter or BookChatActions.UpdateChapterOutline)
            .Select(p => (Proposal: p, ChapterId: p.Target is { } t && t.StartsWith("chapter:") && int.TryParse(t[8..], out var id) ? id : 0))
            .Where(x => x.ChapterId > 0)
            .ToList();
        if (outlined.Count == 0) return;

        var open = (await repo.GetProposalsByStatusAsync(bookId, ChatProposalStatus.Pending))
            .Concat(await repo.GetProposalsByStatusAsync(bookId, ChatProposalStatus.Failed))
            .Concat(prose)
            .Where(o => o.Type is BookChatActions.WriteChapter or BookChatActions.ReviseChapter)
            .ToList();
        var actions = new List<RawChatAction>();
        var lines = new List<string>();
        foreach (var (added, chapterId) in outlined.DistinctBy(x => x.ChapterId))
        {
            var proseTarget = BookContentService.ProseTarget(chapterId);
            if (open.Any(o => o.Target == proseTarget || o.DependsOnProposalId == added.Id)) continue;
            if (await repo.GetChapterAsync(bookId, chapterId) is not { IsArchived: false } chapter) continue;
            var name = chapter.Title.Trim().Length > 0 ? $"Chapter {chapter.Number}: {chapter.Title}" : $"Chapter {chapter.Number}";
            if (chapter.Content.Trim().Length == 0)
            {
                actions.Add(Raw(BookChatActions.WriteChapter, $"Write {name}", chapter: chapter.Number));
                lines.Add($"The outline of {name} is ready — write it when you are.");
            }
            else if (added.Type == BookChatActions.UpdateChapterOutline)
            {
                actions.Add(Raw(BookChatActions.ReviseChapter, $"Revise {name} to follow its new outline", chapter: chapter.Number,
                    instructions: PromptTemplates.BookChatReviseToOutline));
                lines.Add($"{name} is already written — revise it to follow the new outline, or keep it as it is.");
            }
        }
        if (actions.Count > 0) await chat.ProposeAsync(bookId, "➡️ " + string.Join("\n\n", lines), actions);
    }

    /// <summary>Alternatives are exclusive: applying option A of a reply rejects its option B.</summary>
    private async Task RejectOtherOptionsAsync(int bookId, IReadOnlyList<ChatProposal> applying)
    {
        foreach (var group in applying.Where(p => p.OptionGroup is not null).GroupBy(p => p.MessageId))
        {
            var chosen = group.Select(p => p.OptionGroup).ToHashSet();
            foreach (var other in await repo.GetProposalsByStatusAsync(bookId, ChatProposalStatus.Pending))
                if (other.MessageId == group.Key && other.OptionGroup is not null && !chosen.Contains(other.OptionGroup))
                {
                    other.Status = ChatProposalStatus.Rejected;
                    other.ResultNote = "Another alternative was chosen.";
                    await repo.UpdateProposalAsync(other);
                }
        }
    }

    /// <summary>Applies one proposal and records the result. Returns the chapter whose prose it changed, if any.</summary>
    private async Task<int?> ApplyOneAsync(int bookId, ChatProposal p, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (p.DependsOnProposalId is int dependency
            && await repo.GetProposalAsync(bookId, dependency) is not { Status: ChatProposalStatus.Applied })
        {
            await FinishAsync(bookId, p, ChatProposalStatus.Failed, "It needs the change it depends on applied first.");
            return null;
        }

        p.Status = ChatProposalStatus.Applying;
        await repo.UpdateProposalAsync(p);
        await notifier.NotifyBookChatAsync(bookId, p.MessageId, ct);
        try
        {
            var (change, chapterId) = await RunAsync(bookId, p, ct);
            p.BeforeJson = change.Before;
            p.AfterJson = change.After;
            p.AppliedAt = DateTime.UtcNow;
            await FinishAsync(bookId, p, ChatProposalStatus.Applied, change.Note);
            return chapterId;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await FinishAsync(bookId, p, ChatProposalStatus.Failed, "Stopped before it finished.");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Book {BookId}] Applying proposal {ProposalId} ({Type}) failed.", bookId, p.Id, p.Type);
            // A failed save stays queued in the context — drop it, or recording the failure would fail too.
            repo.DiscardPendingChanges();
            await FinishAsync(bookId, p, ChatProposalStatus.Failed, ex.Message);
            return null;
        }
    }

    private async Task FinishAsync(int bookId, ChatProposal p, ChatProposalStatus status, string note)
    {
        // Reloaded after a discarded save: the instance in hand may no longer be the tracked one.
        var tracked = await repo.GetProposalAsync(bookId, p.Id) ?? p;
        if (!ReferenceEquals(tracked, p))
        {
            tracked.BeforeJson = p.BeforeJson; tracked.AfterJson = p.AfterJson; tracked.AppliedAt = p.AppliedAt;
            tracked.BeforeVersionId = p.BeforeVersionId; tracked.AfterVersionId = p.AfterVersionId; tracked.Target = p.Target;
            p = tracked;
        }
        p.Status = status;
        p.ResultNote = note;
        await repo.UpdateProposalAsync(p);
        await notifier.NotifyBookChatAsync(bookId, p.MessageId, CancellationToken.None);
    }

    private async Task<(ContentChange Change, int? ProseChapterId)> RunAsync(int bookId, ChatProposal p, CancellationToken ct)
    {
        var a = ChatActionPayload.FromJson(p.PayloadJson);
        switch (a.Type)
        {
            case BookChatActions.UpdatePremise:
                return (await content.UpdatePremiseAsync(bookId, a.Text ?? ""), null);
            case BookChatActions.UpdateStoryBible:
                return (await content.UpdateStoryBibleAsync(bookId, a.Changes), null);
            case BookChatActions.UpsertCharacter:
                return (await content.UpsertCharacterAsync(bookId, a.Name!, a.Changes), null);
            case BookChatActions.ArchiveCharacter:
                return (await content.ArchiveCharacterAsync(bookId, a.Name!), null);
            case BookChatActions.UpsertPlotThread:
                return (await content.UpsertPlotThreadAsync(bookId, a.Name!, a.Changes), null);
            case BookChatActions.ArchivePlotThread:
                return (await content.ArchivePlotThreadAsync(bookId, a.Name!), null);
            case BookChatActions.AddChapter:
            {
                var change = await content.AddChapterAsync(bookId, a.Changes);
                p.Target = BookContentService.ChapterTarget(change.ChapterId!.Value);
                return (change, null);
            }
            case BookChatActions.UpdateChapterOutline:
                return (await content.UpdateChapterOutlineAsync(bookId, a.ChapterId!.Value, a.Changes), null);
            case BookChatActions.ArchiveChapter:
                return (await content.ArchiveLastChapterAsync(bookId, a.ChapterId!.Value), null);
            case BookChatActions.AddNote:
            {
                var change = await content.AddNoteAsync(bookId, a.Text ?? "");
                p.Target = $"{BookContentService.NoteTarget}:{change.ClarificationId}";
                return (change, null);
            }
            case BookChatActions.WriteChapter:
            {
                var chapterId = a.ChapterId ?? await AddedChapterIdAsync(bookId, p);
                p.Target = BookContentService.ProseTarget(chapterId);
                return (await ChangeProseAsync(bookId, p, chapterId, async () =>
                {
                    state.UpdateRunRole(bookId, AgentRole.Writer, chapterId);
                    await writer.WriteAsync(bookId, chapterId, ct, a.Instructions);
                }, "Wrote"), chapterId);
            }
            case BookChatActions.ReviseChapter:
                return (await ChangeProseAsync(bookId, p, a.ChapterId!.Value, async () =>
                {
                    state.UpdateRunRole(bookId, AgentRole.Editor, a.ChapterId);
                    await editor.RewriteAsync(bookId, a.ChapterId!.Value, [], a.Instructions, finalizeStatus: true, ct);
                }, "Revised"), a.ChapterId);
            case BookChatActions.PatchChapter:
                return (await ChangeProseAsync(bookId, p, a.ChapterId!.Value, async () =>
                {
                    var outcome = await editor.PatchAsync(bookId, a.ChapterId!.Value,
                        [new CheckerIssue("continuity", p.Label, "", a.OriginalText ?? "", a.ReplacementText ?? "")],
                        AgentCreatedBy.BookChat, ct);
                    if (outcome.Applied.Count == 0)
                        throw new InvalidOperationException(
                            $"The text to replace is no longer in the chapter ({outcome.Skipped.FirstOrDefault().Reason ?? "not found"}).");
                }, "Edited"), a.ChapterId);
            default:
                throw new InvalidOperationException($"Unknown action \"{a.Type}\".");
        }
    }

    /// <summary>The chapter a write needs, added by the proposal it depends on.</summary>
    private async Task<int> AddedChapterIdAsync(int bookId, ChatProposal p)
    {
        var added = p.DependsOnProposalId is int id ? await repo.GetProposalAsync(bookId, id) : null;
        return added?.Target is { } t && t.StartsWith("chapter:") && int.TryParse(t[8..], out var chapterId)
            ? chapterId
            : throw new InvalidOperationException("The chapter to write was not added.");
    }

    /// <summary>Runs a prose change, recording the versions before and after so it can be undone.</summary>
    private async Task<ContentChange> ChangeProseAsync(int bookId, ChatProposal p, int chapterId, Func<Task> change, string verb)
    {
        var before = await repo.GetChapterAsync(bookId, chapterId)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");
        AgentBase.EnsureNotArchived(before);
        var beforeContent = before.Content;
        p.BeforeVersionId = await content.ActiveVersionIdAsync(bookId, chapterId);
        await change();
        p.AfterVersionId = await content.ActiveVersionIdAsync(bookId, chapterId);
        var after = await repo.GetChapterAsync(bookId, chapterId) ?? before;
        if (after.Status != ChapterStatus.Done)
        {
            after.Status = ChapterStatus.Done;
            await repo.UpdateChapterAsync(after);
            await notifier.NotifyChapterUpdatedAsync(bookId, chapterId);
        }
        return new ContentChange($"{verb} Chapter {after.Number} ({WordCount(after.Content)} words).", beforeContent, after.Content, chapterId);
    }

    private static int WordCount(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// After a chapter's prose changed: summarize it, check it (written or revised chapters only), and post what was
    /// found as proposals — patches and a revision for the checker's issues, and the characters and plot-thread
    /// changes the chapter introduced. Each step that fails is reported and skipped; the prose is already saved.
    /// </summary>
    private async Task FollowUpAsync(int bookId, int chapterId, bool check, CancellationToken ct)
    {
        var chapter = await repo.GetChapterAsync(bookId, chapterId);
        if (chapter is null || chapter.IsArchived || chapter.Content.Length == 0) return;
        var lines = new List<string>();
        var actions = new List<RawChatAction>();

        state.UpdateRunRole(bookId, AgentRole.ChapterSummarizer, chapterId);
        try
        {
            if (await summarizer.SummarizeAsync(bookId, chapterId, ct) is { } summary)
            {
                foreach (var c in summary.NewCharacters)
                    actions.Add(Raw(BookChatActions.UpsertCharacter, $"Add {c.Name}, who appears in Chapter {chapter.Number}", name: c.Name,
                        changes: [new("role", c.Role.ToString()), new("notes", c.Description), new("firstAppearanceChapterNumber", chapter.Number.ToString())]));
                var statuses = (await repo.GetPlotThreadsAsync(bookId))
                    .ToDictionary(t => t.Name, t => t.Status, StringComparer.OrdinalIgnoreCase);
                // A "change" to the status the thread already has is no change.
                foreach (var t in summary.ThreadChanges.Where(t => statuses.GetValueOrDefault(t.Name) != t.Status))
                {
                    List<KeyValuePair<string, string>> changes = [new("status", t.Status.ToString())];
                    if (t.Status == PlotThreadStatus.Resolved) changes.Add(new("resolvedChapterNumber", chapter.Number.ToString()));
                    actions.Add(Raw(BookChatActions.UpsertPlotThread, $"Mark {t.Name} {t.Status.ToString().ToLowerInvariant()}: {t.Note}",
                        name: t.Name, changes: changes));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Book {BookId}] Summarizing chapter {ChapterId} failed.", bookId, chapterId);
            lines.Add($"⚠️ The chapter summary could not be written: {ex.Message}");
        }

        if (check)
        {
            state.UpdateRunRole(bookId, AgentRole.ContinuityChecker, chapterId);
            try
            {
                var result = await checker.CheckAsync(bookId, chapterId, ct);
                var patches = result.HasIssues ? EditorAgent.SelectPatches(result) : [];
                var rewrites = result.HasIssues ? EditorAgent.SelectRewrites(result) : [];
                foreach (var issue in patches.Take(BookChatActions.MaxActions - 1))
                    actions.Add(Raw(BookChatActions.PatchChapter, issue.Description, chapter: chapter.Number,
                        originalText: issue.OriginalText, replacementText: issue.ReplacementText));
                if (rewrites.Count > 0)
                    actions.Add(Raw(BookChatActions.ReviseChapter, $"Revise Chapter {chapter.Number} for {rewrites.Count} issue(s)",
                        chapter: chapter.Number, instructions: PromptTemplates.RewriteInstructions(rewrites, null)));
                lines.Add(patches.Length + rewrites.Count == 0
                    ? "The continuity check found no issues."
                    : $"The continuity check found {patches.Length + rewrites.Count} issue(s): {result.Summary}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Book {BookId}] Checking chapter {ChapterId} failed.", bookId, chapterId);
                lines.Add($"⚠️ The continuity check failed: {ex.Message}");
            }
        }

        var header = $"✍️ Chapter {chapter.Number} \"{chapter.Title}\" is {(check ? "written" : "updated")} ({WordCount(chapter.Content)} words).";
        if (actions.Count > 0) lines.Add("Proposed follow-ups are below — apply the ones you want.");
        await chat.ProposeAsync(bookId, string.Join("\n\n", lines.Prepend(header)), actions);

        if (check && (await repo.GetByIdAsync(bookId))?.IllustrationsEnabled == true)
        {
            state.UpdateRunRole(bookId, AgentRole.IllustrationPlanner, chapterId);
            var fresh = await repo.GetChapterAsync(bookId, chapterId);
            if (fresh is not null) await illustrationPlanner.PlanChapterOrNoteAsync(bookId, fresh, ct);
        }
    }

    private static RawChatAction Raw(string type, string label, int? chapter = null, string? name = null,
        List<KeyValuePair<string, string>>? changes = null, string? instructions = null,
        string? originalText = null, string? replacementText = null) =>
        new(type, label.Length > 300 ? label[..300] + "…" : label, chapter, name, changes ?? [], instructions, originalText, replacementText, null, null);

    // ── Undo ────────────────────────────────────────────────────────────────────

    public async Task UndoAsync(int bookId, int proposalId, CancellationToken ct)
    {
        var p = await repo.GetProposalAsync(bookId, proposalId)
            ?? throw new InvalidOperationException($"Proposal {proposalId} not found.");
        if (await UndoBlockerAsync(bookId, p) is { } blocker) throw new InvalidOperationException(blocker);

        var a = ChatActionPayload.FromJson(p.PayloadJson);
        var before = p.BeforeJson ?? "";
        try
        {
            ContentChange change = a.Type switch
            {
                BookChatActions.UpdatePremise => await content.UpdatePremiseAsync(bookId, before),
                BookChatActions.UpdateStoryBible => await content.RestoreStoryBibleAsync(bookId, before),
                BookChatActions.UpsertCharacter or BookChatActions.ArchiveCharacter =>
                    await content.RestoreCharacterAsync(bookId, a.Name!, before),
                BookChatActions.UpsertPlotThread or BookChatActions.ArchivePlotThread =>
                    await content.RestorePlotThreadAsync(bookId, a.Name!, before),
                BookChatActions.AddChapter => await content.ArchiveLastChapterAsync(bookId, TargetId(p, "chapter:")),
                BookChatActions.UpdateChapterOutline => await content.UpdateChapterOutlineAsync(bookId, a.ChapterId!.Value,
                    BookContentRender.Parse(before).Where(kv => kv.Key != "number").ToDictionary()),
                BookChatActions.ArchiveChapter => await content.RestoreLastChapterAsync(bookId, a.ChapterId!.Value),
                BookChatActions.AddNote => await content.ArchiveNoteAsync(bookId, TargetId(p, "note:")),
                _ => await content.RestoreProseAsync(bookId, TargetId(p, "prose:"), p.BeforeVersionId, ct),
            };
            p.Status = ChatProposalStatus.Undone;
            p.ResultNote = $"Undone: {change.Note}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Book {BookId}] Undoing proposal {ProposalId} failed.", bookId, p.Id);
            repo.DiscardPendingChanges();
            p = await repo.GetProposalAsync(bookId, proposalId) ?? p;
            p.ResultNote = $"Undo failed: {ex.Message}";
        }
        await repo.UpdateProposalAsync(p);
        await notifier.NotifyBookChatAsync(bookId, p.MessageId, ct);
    }

    private static int TargetId(ChatProposal p, string prefix) =>
        p.Target is { } t && t.StartsWith(prefix) && int.TryParse(t[prefix.Length..], out var id)
            ? id
            : throw new InvalidOperationException("The change has no recorded target to undo.");
}
