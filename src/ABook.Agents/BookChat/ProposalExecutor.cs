using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.Logging;

namespace ABook.Agents;

/// <summary>
/// Applies the proposals the author approved in the book chat, and undoes applied ones. Quick changes (bible,
/// characters, threads, outlines, notes) run first, in reply order; prose changes (write, revise, patch) run
/// after them, and each written or revised chapter is then summarized and checked, with what the check finds
/// posted back to the chat as new proposals. Chapter writes and revisions go one full cycle at a time: the later
/// ones of a batch are queued and each starts once the chapter before it is checked and its follow-ups settled.
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
        if (BookChatActions.IsStep(p.Type)) return "A check or summary changes nothing in the book — there is nothing to undo.";
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

    /// <summary>
    /// Whether <paramref name="p"/> can be applied now: pending, failed or queued (applying a queued one starts it
    /// early) — or a step already run, which may run again.
    /// </summary>
    public static bool CanApply(ChatProposal p) =>
        p.Status is ChatProposalStatus.Pending or ChatProposalStatus.Failed or ChatProposalStatus.Queued
        || (p.Status == ChatProposalStatus.Applied && BookChatActions.IsStep(p.Type));

    /// <summary>Writing and revising a chapter each go through a full cycle — written, checked, reviewed — one chapter at a time.</summary>
    public static bool IsCycle(string type) => type is BookChatActions.WriteChapter or BookChatActions.ReviseChapter;

    /// <summary>
    /// Applies the proposals, then starts every queued write or revision whose turn has come (see
    /// <see cref="NextQueuedAsync"/>) — in this run, one after another, until one waits for the author.
    /// </summary>
    public async Task ApplyAsync(int bookId, IReadOnlyList<int> proposalIds, CancellationToken ct)
    {
        await ApplyBatchAsync(bookId, proposalIds, ct);
        while (await NextQueuedAsync(bookId) is { } next)
            await ApplyBatchAsync(bookId, [next.Id], ct);
    }

    /// <summary>
    /// The queued write or revision whose turn has come: the one before it is applied and nothing proposed since then
    /// (its check's fixes, the characters and threads its summary found, chat replies about it) is still open. A queued
    /// one whose predecessor did not go through (rejected, failed, undone) goes back to Pending for the author to decide.
    /// </summary>
    public async Task<ChatProposal?> NextQueuedAsync(int bookId)
    {
        foreach (var q in await repo.GetProposalsByStatusAsync(bookId, ChatProposalStatus.Queued))
        {
            var before = q.QueuedAfterProposalId is int id ? await repo.GetProposalAsync(bookId, id) : null;
            switch (before?.Status)
            {
                case ChatProposalStatus.Pending or ChatProposalStatus.Queued or ChatProposalStatus.Applying:
                    continue;
                case ChatProposalStatus.Applied when before.AppliedAt is DateTime appliedAt:
                    if ((await repo.GetOpenProposalsSinceAsync(bookId, appliedAt)).Count > 0) continue;
                    return q;
                default:
                    q.Status = ChatProposalStatus.Pending;
                    q.ResultNote = before is null
                        ? "Not started: what it waited for is gone. Apply it when you are ready."
                        : $"Not started: \"{before.Label}\" did not go through. Apply it when you are ready.";
                    await repo.UpdateProposalAsync(q);
                    await notifier.NotifyBookChatAsync(bookId, q.MessageId, CancellationToken.None);
                    continue;
            }
        }
        return null;
    }

    private async Task ApplyBatchAsync(int bookId, IReadOnlyList<int> proposalIds, CancellationToken ct)
    {
        var proposals = (await repo.GetProposalsAsync(bookId, proposalIds)).Where(CanApply).ToList();
        if (proposals.Count == 0) return;

        await RejectOtherOptionsAsync(bookId, proposals);

        var steps = proposals.Where(p => BookChatActions.IsStep(p.Type)).ToList();
        var quick = proposals.Where(p => !BookChatActions.ProseActions.Contains(p.Type) && !BookChatActions.IsStep(p.Type)).ToList();
        var prose = proposals.Where(p => BookChatActions.ProseActions.Contains(p.Type)).ToList();
        foreach (var p in quick) await ApplyOneAsync(bookId, p, ct);

        // Offered before the prose runs, so these offers don't hold up the chapters queued below.
        await OfferNextStepsAsync(bookId, quick, prose);

        // One chapter may shape the next: only the first write/revision runs now. Each later one is queued behind
        // the one before it and starts once that chapter is written, checked and its follow-ups are settled.
        var cycles = prose.Where(p => IsCycle(p.Type)).ToList();
        for (var i = 1; i < cycles.Count; i++)
        {
            var q = cycles[i];
            q.Status = ChatProposalStatus.Queued;
            q.QueuedAfterProposalId = cycles[i - 1].Id;
            q.ResultNote = $"Queued: starts after \"{cycles[i - 1].Label}\" is written and checked, and its follow-ups are applied or rejected.";
            await repo.UpdateProposalAsync(q);
            await notifier.NotifyBookChatAsync(bookId, q.MessageId, ct);
        }
        if (cycles.Count > 1) prose = prose.Except(cycles.Skip(1)).ToList();

        // Writing the first chapter moves the book to its writing stage.
        if (prose.Any(p => p.Type == BookChatActions.WriteChapter)
            && await repo.GetByIdAsync(bookId) is { Mode: BookMode.Interactive, StoryBibleLockedAt: null }
            && await content.LockStoryBibleAsync(bookId))
            await chat.AddEventAsync(bookId, "📌 The premise and Story Bible are now locked — writing has started.");

        // Chapter → whether it needs a continuity check (written or revised; a patch alone only needs a new summary).
        var changed = new Dictionary<int, bool>();
        // Several patches of one chapter are applied as one edit (one chapter version), where the first of them stood.
        var patchGroups = prose.Where(p => p.Type == BookChatActions.PatchChapter)
            .GroupBy(p => ChatActionPayload.FromJson(p.PayloadJson).ChapterId)
            .Where(g => g.Key is not null && g.Count() > 1)
            .ToDictionary(g => g.First().Id, g => g.ToList());
        var grouped = patchGroups.Values.SelectMany(g => g).Select(p => p.Id).ToHashSet();
        foreach (var p in prose)
        {
            int? chapterId;
            if (patchGroups.TryGetValue(p.Id, out var group)) chapterId = await ApplyPatchGroupAsync(bookId, group, ct);
            else if (grouped.Contains(p.Id)) continue;
            else chapterId = await ApplyOneAsync(bookId, p, ct);
            if (chapterId is int id)
                changed[id] = changed.GetValueOrDefault(id) || p.Type != BookChatActions.PatchChapter;
        }

        foreach (var (chapterId, check) in changed)
            await FollowUpAsync(bookId, chapterId, check, ct);

        // Steps run last, so they see this batch's changes. A chapter whose prose changed above was already
        // summarized (and, when written or revised, checked) by its follow-up — that step is not run twice.
        foreach (var p in steps)
        {
            var chapterId = ChatActionPayload.FromJson(p.PayloadJson).ChapterId;
            var covered = chapterId is int id && changed.TryGetValue(id, out var checkedAlready)
                          && (p.Type == BookChatActions.SummarizeChapter || (p.Type == BookChatActions.CheckChapter && checkedAlready));
            if (!covered) { await ApplyOneAsync(bookId, p, ct); continue; }
            p.AppliedAt = DateTime.UtcNow;
            await FinishAsync(bookId, p, ChatProposalStatus.Applied, "Already done after the chapter changed — see its follow-up.");
        }
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

    /// <summary>
    /// Applies several patches of one chapter as a single edit: one chapter version, recorded on every patch that
    /// applied (same versions and time, so undoing one undoes them all). A patch that no longer locates, or overlaps
    /// another, fails on its own. Returns the chapter when its prose changed.
    /// </summary>
    private async Task<int?> ApplyPatchGroupAsync(int bookId, List<ChatProposal> group, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var chapterId = ChatActionPayload.FromJson(group[0].PayloadJson).ChapterId!.Value;
        foreach (var p in group)
        {
            p.Status = ChatProposalStatus.Applying;
            await repo.UpdateProposalAsync(p);
        }
        await notifier.NotifyBookChatAsync(bookId, group[0].MessageId, ct);

        var issues = group.Select(p =>
        {
            var a = ChatActionPayload.FromJson(p.PayloadJson);
            return new CheckerIssue("continuity", p.Label, "", a.OriginalText ?? "", a.ReplacementText ?? "");
        }).ToList();
        try
        {
            EditorAgent.PatchOutcome outcome = null!;
            var change = await ChangeProseAsync(bookId, group[0], chapterId, async () =>
            {
                outcome = await editor.PatchAsync(bookId, chapterId, issues, AgentCreatedBy.BookChat, ct);
                if (outcome.Applied.Count == 0)
                    throw new InvalidOperationException(
                        $"None of the texts to replace are in the chapter any more ({outcome.Skipped.FirstOrDefault().Reason ?? "not found"}).");
            }, "Edited");

            var (beforeVersion, afterVersion) = (group[0].BeforeVersionId, group[0].AfterVersionId);
            var appliedAt = DateTime.UtcNow;
            var others = outcome.Applied.Count - 1;
            for (var i = 0; i < group.Count; i++)
            {
                var p = group[i];
                if (outcome.Applied.Any(a => ReferenceEquals(a, issues[i])))
                {
                    p.Target = BookContentService.ProseTarget(chapterId);
                    p.BeforeVersionId = beforeVersion;
                    p.AfterVersionId = afterVersion;
                    p.BeforeJson = change.Before;
                    p.AfterJson = change.After;
                    p.AppliedAt = appliedAt;
                    await FinishAsync(bookId, p, ChatProposalStatus.Applied,
                        others > 0 ? $"{change.Note} One edit with {others} other fix(es) — undoing it undoes them all." : change.Note);
                }
                else
                {
                    var reason = outcome.Skipped.FirstOrDefault(s => ReferenceEquals(s.Issue, issues[i])).Reason ?? "not found";
                    p.BeforeVersionId = p.AfterVersionId = null;
                    await FinishAsync(bookId, p, ChatProposalStatus.Failed, $"Not applied: {reason}.");
                }
            }
            return chapterId;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            foreach (var p in group) await FinishAsync(bookId, p, ChatProposalStatus.Failed, "Stopped before it finished.");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Book {BookId}] Applying {Count} patches to chapter {ChapterId} failed.", bookId, group.Count, chapterId);
            repo.DiscardPendingChanges();
            foreach (var p in group)
            {
                p.BeforeVersionId = p.AfterVersionId = null;
                await FinishAsync(bookId, p, ChatProposalStatus.Failed, ex.Message);
            }
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
                var change = a.AtStart || a.AfterChapterId is not null
                    ? await content.InsertChapterAsync(bookId, a.AtStart ? null : a.AfterChapterId, a.Changes)
                    : await content.AddChapterAsync(bookId, a.Changes);
                p.Target = BookContentService.ChapterTarget(change.ChapterId!.Value);
                return (change, null);
            }
            case BookChatActions.MoveChapter:
            {
                var chapters = (await repo.GetChaptersAsync(bookId)).ToList();
                var index = chapters.FindIndex(c => c.Id == a.ChapterId);
                if (index < 0) throw new InvalidOperationException("The chapter no longer exists.");
                if (chapters[index].Content.Trim().Length > 0)
                    throw new InvalidOperationException($"Chapter {chapters[index].Number} has been written — only chapters not written yet can be moved.");
                // Where it was, for undo.
                var previous = index > 0 ? chapters[index - 1] : null;
                p.PayloadJson = (a with { UndoAfterChapterId = previous?.Id, UndoAtStart = previous is null }).ToJson();
                return (await content.MoveChapterAsync(bookId, a.ChapterId!.Value, a.AtStart ? null : a.AfterChapterId), null);
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
            case BookChatActions.UpdateNote:
                return (await content.UpdateNoteAsync(bookId, TargetId(p, "note:"), a.Text ?? ""), null);
            case BookChatActions.ArchiveNote:
                return (await content.ArchiveNoteAsync(bookId, TargetId(p, "note:")), null);
            case BookChatActions.SetPartRequests:
                return (await content.SetPartRequestsAsync(bookId, a.Text == "on"), null);
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
            case BookChatActions.CheckOutline:
                return (await CheckOutlineStepAsync(bookId, a.ChapterId ?? await AddedChapterIdAsync(bookId, p), ct), null);
            case BookChatActions.CheckChapter:
            case BookChatActions.SummarizeChapter:
                return (await ChapterStepAsync(bookId, a.ChapterId!.Value, a.Type == BookChatActions.CheckChapter, ct), null);
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

        try { await SummarizeIntoAsync(bookId, chapter, actions, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Book {BookId}] Summarizing chapter {ChapterId} failed.", bookId, chapterId);
            lines.Add($"⚠️ The chapter summary could not be written: {ex.Message}");
        }

        if (check)
        {
            try { lines.Add(await CheckIntoAsync(bookId, chapter, actions, ct)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Book {BookId}] Checking chapter {ChapterId} failed.", bookId, chapterId);
                lines.Add($"⚠️ The continuity check failed: {ex.Message}");
            }
        }

        var header = $"✍️ Chapter {chapter.Number} \"{chapter.Title}\" is {(check ? "written" : "updated")} ({WordCount(chapter.Content)} words).";
        if (actions.Count > 0) lines.Add("Proposed follow-ups are below — apply the ones you want.");
        if (actions.Count > 0 && (await repo.GetProposalsByStatusAsync(bookId, ChatProposalStatus.Queued)).FirstOrDefault() is { } next)
            lines.Add($"⏭ Next: \"{next.Label}\" — it starts once every suggestion below is applied or rejected.");
        await chat.ProposeAsync(bookId, string.Join("\n\n", lines.Prepend(header)), actions);

        if (check && (await repo.GetByIdAsync(bookId))?.IllustrationsEnabled == true)
        {
            state.UpdateRunRole(bookId, AgentRole.IllustrationPlanner, chapterId);
            var fresh = await repo.GetChapterAsync(bookId, chapterId);
            if (fresh is not null) await illustrationPlanner.PlanChapterOrNoteAsync(bookId, fresh, ct);
        }
    }

    /// <summary>Rewrites the chapter's summary and adds proposals for the new characters and plot-thread changes it found.</summary>
    private async Task SummarizeIntoAsync(int bookId, Chapter chapter, List<RawChatAction> actions, CancellationToken ct)
    {
        state.UpdateRunRole(bookId, AgentRole.ChapterSummarizer, chapter.Id);
        if (await summarizer.SummarizeAsync(bookId, chapter.Id, ct) is not { } summary) return;
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

    /// <summary>Runs the continuity check on the chapter and adds patches and a revision for what it found. Returns the result line.</summary>
    private async Task<string> CheckIntoAsync(int bookId, Chapter chapter, List<RawChatAction> actions, CancellationToken ct)
    {
        state.UpdateRunRole(bookId, AgentRole.ContinuityChecker, chapter.Id);
        var result = await checker.CheckAsync(bookId, chapter.Id, ct);
        var patches = result.HasIssues ? EditorAgent.SelectPatches(result) : [];
        var rewrites = result.HasIssues ? EditorAgent.SelectRewrites(result) : [];
        foreach (var issue in patches.Take(BookChatActions.MaxActions - 1))
            actions.Add(Raw(BookChatActions.PatchChapter, issue.Description, chapter: chapter.Number,
                originalText: issue.OriginalText, replacementText: issue.ReplacementText));
        if (rewrites.Count > 0)
            actions.Add(Raw(BookChatActions.ReviseChapter, $"Revise Chapter {chapter.Number} for {rewrites.Count} issue(s)",
                chapter: chapter.Number, instructions: PromptTemplates.RewriteInstructions(rewrites, null)));
        return patches.Length + rewrites.Count == 0
            ? "The continuity check found no issues."
            : $"The continuity check found {patches.Length + rewrites.Count} issue(s): {result.Summary}";
    }

    /// <summary>
    /// The check_chapter / summarize_chapter step: re-runs the continuity check or the summary of a written chapter
    /// on request and posts what it found as proposals. Failures propagate, so the step shows as Failed and can be
    /// run again.
    /// </summary>
    private async Task<ContentChange> ChapterStepAsync(int bookId, int chapterId, bool check, CancellationToken ct)
    {
        var chapter = AgentBase.EnsureNotArchived(await repo.GetChapterAsync(bookId, chapterId)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found."));
        if (chapter.Content.Trim().Length == 0) throw new InvalidOperationException($"Chapter {chapter.Number} is not written yet.");
        // The checker reviews finished chapters only; any other status would turn it into a whole-book review.
        if (check && chapter.Status is not (ChapterStatus.Done or ChapterStatus.Review))
            throw new InvalidOperationException($"Chapter {chapter.Number} is not finished (it is {chapter.Status}).");

        var actions = new List<RawChatAction>();
        var name = $"Chapter {chapter.Number} \"{chapter.Title}\"";
        string text, note;
        if (check)
        {
            var line = await CheckIntoAsync(bookId, chapter, actions, ct);
            text = $"🔎 Continuity check of {name}: {line}";
            note = actions.Count == 0 ? "No issues found." : $"Found {actions.Count} fix(es) — see the message below.";
        }
        else
        {
            await SummarizeIntoAsync(bookId, chapter, actions, ct);
            text = $"📝 {name} is summarized again." + (actions.Count == 0 ? " It introduces no new characters or plot-thread changes." : "");
            note = actions.Count == 0 ? "Summary rewritten." : $"Summary rewritten; {actions.Count} update(s) proposed below.";
        }
        if (actions.Count > 0) text += "\n\nProposed follow-ups are below — apply the ones you want.";
        await chat.ProposeAsync(bookId, text, actions);
        return new ContentChange(note, "", "", chapterId);
    }

    /// <summary>
    /// The check_outline step: checks the chapter's outline against what the book has established and, when it
    /// contradicts it, proposes the corrected outline. Nothing is changed until the author applies that proposal.
    /// </summary>
    private async Task<ContentChange> CheckOutlineStepAsync(int bookId, int chapterId, CancellationToken ct)
    {
        var chapter = AgentBase.EnsureNotArchived(await repo.GetChapterAsync(bookId, chapterId)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found."));
        if (chapter.Outline.Trim().Length == 0) throw new InvalidOperationException($"Chapter {chapter.Number} has no outline to check.");

        state.UpdateRunRole(bookId, AgentRole.ContinuityChecker, chapterId);
        var result = await checker.CheckOutlineAsync(bookId, chapterId, ct);
        var name = $"Chapter {chapter.Number} \"{chapter.Title}\"";
        if (result.Issues is null)
        {
            await chat.ProposeAsync(bookId, $"🔎 The outline of {name} is consistent with the book — no contradictions found.", []);
            return new ContentChange("No issues found.", "", "", chapterId);
        }

        List<RawChatAction> actions = result.FixedOutline is { } fixedOutline
            ? [Raw(BookChatActions.UpdateChapterOutline, $"Fix the outline of Chapter {chapter.Number}", chapter: chapter.Number,
                changes: [new("outline", fixedOutline)])]
            : [];
        var text = $"🔎 The outline check of {name} found contradictions with the book:\n\n{result.Issues}" +
                   (actions.Count > 0
                       ? "\n\nA corrected outline is proposed below."
                       : "\n\n⚠️ No corrected outline could be written — ask me to fix it, or edit the outline yourself.");
        await chat.ProposeAsync(bookId, text, actions);
        return new ContentChange(actions.Count > 0 ? "Found contradictions — a fix is proposed below." : "Found contradictions.",
            "", "", chapterId);
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
                BookChatActions.MoveChapter => await content.MoveChapterAsync(bookId, a.ChapterId!.Value,
                    a.UndoAtStart ? null : a.UndoAfterChapterId),
                BookChatActions.AddNote => await content.ArchiveNoteAsync(bookId, TargetId(p, "note:")),
                BookChatActions.UpdateNote => await content.UpdateNoteAsync(bookId, TargetId(p, "note:"), before),
                BookChatActions.ArchiveNote => await content.RestoreNoteAsync(bookId, TargetId(p, "note:")),
                BookChatActions.SetPartRequests => await content.SetPartRequestsAsync(bookId, before == "on"),
                _ => await content.RestoreProseAsync(bookId, TargetId(p, "prose:"), p.BeforeVersionId, ct),
            };
            p.Status = ChatProposalStatus.Undone;
            p.ResultNote = $"Undone: {change.Note}";

            // Patches applied as one edit share its versions: restoring the version before it undid them all.
            if (a.Type == BookChatActions.PatchChapter && p.AfterVersionId is int afterVersion)
                foreach (var sibling in await repo.GetProposalsByStatusAsync(bookId, ChatProposalStatus.Applied))
                    if (sibling.Id != p.Id && sibling.Type == BookChatActions.PatchChapter && sibling.AfterVersionId == afterVersion)
                    {
                        sibling.Status = ChatProposalStatus.Undone;
                        sibling.ResultNote = $"Undone together with \"{p.Label}\" (they were one edit).";
                        await repo.UpdateProposalAsync(sibling);
                        await notifier.NotifyBookChatAsync(bookId, sibling.MessageId, ct);
                    }
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
