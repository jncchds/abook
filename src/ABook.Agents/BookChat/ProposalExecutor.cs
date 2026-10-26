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
    PlannerAgent planner,
    ILogger<ProposalExecutor> logger)
{
    /// <summary>Types whose preview shows the target as it read when proposed — applying after it changed needs confirming.</summary>
    private static readonly HashSet<string> StaleChecked =
    [
        BookChatActions.UpdatePremise, BookChatActions.UpdateStoryBible,
        BookChatActions.UpsertCharacter, BookChatActions.ArchiveCharacter,
        BookChatActions.UpsertPlotThread, BookChatActions.ArchivePlotThread,
        BookChatActions.UpdateChapterOutline, BookChatActions.ArchiveChapter,
        BookChatActions.UpdateSectionPlan, BookChatActions.ArchiveSection,
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
        if (BookChatActions.IsStep(p.Type)) return "A check, summary or breakdown changes nothing in the book — there is nothing to undo.";
        if (p.Type == BookChatActions.WriteSection)
            return "Writing a section only queued its chapters — undo each chapter's writing on its own card.";
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
        // Undoing an added section removes it with its chapters: not while one of them holds prose.
        if (p.Type == BookChatActions.AddSection && p.Target is { } st && st.StartsWith("section:")
            && int.TryParse(st[8..], out var sectionId)
            && (await repo.GetChaptersAsync(bookId)).FirstOrDefault(c => c.SectionId == sectionId && c.Content.Trim().Length > 0) is { } writtenChapter)
            return $"Chapter {writtenChapter.Number} of this section has been written — undo its writing first.";
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
        var sectionWrites = proposals.Where(p => p.Type == BookChatActions.WriteSection).ToList();
        var quick = proposals.Where(p => !BookChatActions.ProseActions.Contains(p.Type) && !BookChatActions.IsStep(p.Type)
                                         && p.Type != BookChatActions.WriteSection).ToList();
        var prose = proposals.Where(p => BookChatActions.ProseActions.Contains(p.Type)).ToList();
        foreach (var p in quick) await ApplyOneAsync(bookId, p, ct);

        // A section write becomes one write_chapter per unwritten chapter (after the quick changes, so chapters they
        // added are included), queued with the batch's other writes below.
        foreach (var p in sectionWrites) prose.AddRange(await ExpandSectionWriteAsync(bookId, p, prose, ct));

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
    /// The write_section proposal: one write_chapter proposal per chapter of the section not written yet (and not
    /// already written by this batch), posted as a message of their own. Returns them for the caller's queue; the
    /// section write itself is Applied once they are posted, or Failed when there is nothing left to write.
    /// </summary>
    private async Task<IReadOnlyList<ChatProposal>> ExpandSectionWriteAsync(int bookId, ChatProposal p, IReadOnlyList<ChatProposal> batch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        p.Status = ChatProposalStatus.Applying;
        await repo.UpdateProposalAsync(p);
        await notifier.NotifyBookChatAsync(bookId, p.MessageId, ct);
        try
        {
            var a = ChatActionPayload.FromJson(p.PayloadJson);
            var section = a.SectionId is int sectionId && await repo.GetSectionAsync(bookId, sectionId) is { IsArchived: false } s
                ? s
                : throw new InvalidOperationException("The section no longer exists.");
            var inBatch = batch.Where(o => o.Type == BookChatActions.WriteChapter)
                .Select(o => ChatActionPayload.FromJson(o.PayloadJson).ChapterId).OfType<int>().ToHashSet();
            var chapters = (await repo.GetChaptersAsync(bookId))
                .Where(c => c.SectionId == section.Id && c.Content.Trim().Length == 0 && !inBatch.Contains(c.Id))
                .ToList();
            if (chapters.Count == 0)
                throw new InvalidOperationException($"Every chapter of Section {section.Number} is written (or being written in this batch).");

            var actions = chapters.Select(c => Raw(BookChatActions.WriteChapter, $"Write Chapter {c.Number}: {c.Title}",
                chapter: c.Number, instructions: a.Instructions)).ToList();
            var message = await chat.ProposeAsync(bookId,
                $"✍️ Writing Section {section.Number} \"{section.Title}\": {chapters.Count} chapter(s), one at a time — each starts " +
                "once the one before it is written and checked, and its follow-ups are applied or rejected.",
                actions, maxActions: actions.Count);
            p.AppliedAt = DateTime.UtcNow;
            await FinishAsync(bookId, p, ChatProposalStatus.Applied, $"Queued {chapters.Count} chapter(s) of Section {section.Number} — see the message below.");
            return message.Proposals.OrderBy(o => o.Ordinal).ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await FinishAsync(bookId, p, ChatProposalStatus.Failed, "Stopped before it finished.");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Book {BookId}] Expanding section write {ProposalId} failed.", bookId, p.Id);
            repo.DiscardPendingChanges();
            await FinishAsync(bookId, p, ChatProposalStatus.Failed, ex.Message);
            return [];
        }
    }

    /// <summary>
    /// The obvious next step after an outline is settled, offered as a card without asking the chat model: write a
    /// chapter whose outline was added or changed and that has no prose yet (write the section when several chapters of
    /// one section were outlined), revise a written one to its new outline, or break a new section planned for several
    /// chapters down. Skipped when this batch already changed that chapter's prose or a card for it is still open.
    /// </summary>
    private async Task OfferNextStepsAsync(int bookId, IReadOnlyList<ChatProposal> quick, IReadOnlyList<ChatProposal> prose)
    {
        var applied = quick.Where(p => p.Status == ChatProposalStatus.Applied).ToList();
        var outlined = applied
            .Where(p => p.Type is BookChatActions.AddChapter or BookChatActions.UpdateChapterOutline)
            .Select(p => (Proposal: p, ChapterId: TargetIdOrZero(p, "chapter:")))
            .Where(x => x.ChapterId > 0)
            .ToList();
        var addedSections = applied
            .Where(p => p.Type == BookChatActions.AddSection)
            .Select(p => (Proposal: p, SectionId: TargetIdOrZero(p, "section:")))
            .Where(x => x.SectionId > 0)
            .ToList();
        if (outlined.Count == 0 && addedSections.Count == 0) return;

        var open = (await repo.GetProposalsByStatusAsync(bookId, ChatProposalStatus.Pending))
            .Concat(await repo.GetProposalsByStatusAsync(bookId, ChatProposalStatus.Failed))
            .Concat(prose)
            .Where(o => o.Type is BookChatActions.WriteChapter or BookChatActions.ReviseChapter
                or BookChatActions.WriteSection or BookChatActions.BreakDownSection)
            .ToList();
        bool OpenForSection(int sectionId) => open.Any(o => o.Type is BookChatActions.WriteSection or BookChatActions.BreakDownSection
                                                            && ChatActionPayload.FromJson(o.PayloadJson).SectionId == sectionId);
        var actions = new List<RawChatAction>();
        var lines = new List<string>();
        var allChapters = (await repo.GetChaptersAsync(bookId)).ToList();

        // A new section: break it down when it is planned for several chapters, otherwise write its chapter.
        foreach (var (added, sectionId) in addedSections)
        {
            if (open.Any(o => o.DependsOnProposalId == added.Id) || OpenForSection(sectionId)) continue;
            if (await repo.GetSectionAsync(bookId, sectionId) is not { IsArchived: false } section) continue;
            var own = allChapters.Where(c => c.SectionId == sectionId).ToList();
            if (own.Count == 1 && section.PlannedChapters > 1 && own[0].Content.Trim().Length == 0)
            {
                actions.Add(Raw(BookChatActions.BreakDownSection, $"Break Section {section.Number} down into {section.PlannedChapters} chapters",
                    section: section.Number));
                lines.Add($"Section {section.Number} \"{section.Title}\" is planned for {section.PlannedChapters} chapters — break it down into them when you are ready.");
            }
            else if (own.Count > 0) outlined.Add((added, own[0].Id));
        }

        var candidates = new List<(ChatProposal Proposal, Chapter Chapter)>();
        foreach (var (added, chapterId) in outlined.DistinctBy(x => x.ChapterId))
        {
            var proseTarget = BookContentService.ProseTarget(chapterId);
            if (open.Any(o => o.Target == proseTarget || o.DependsOnProposalId == added.Id)) continue;
            if (allChapters.FirstOrDefault(c => c.Id == chapterId) is not { } chapter) continue;
            candidates.Add((added, chapter));
        }

        // Several chapters of one section outlined at once (a breakdown): one card to write the section.
        var sectionCards = candidates.Where(x => x.Chapter.Content.Trim().Length == 0 && x.Chapter.SectionId is not null)
            .GroupBy(x => x.Chapter.SectionId!.Value)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();
        foreach (var sectionId in sectionCards)
        {
            if (OpenForSection(sectionId) || await repo.GetSectionAsync(bookId, sectionId) is not { IsArchived: false } section) continue;
            actions.Add(Raw(BookChatActions.WriteSection, $"Write Section {section.Number}: {section.Title}", section: section.Number));
            lines.Add($"The chapters of Section {section.Number} \"{section.Title}\" are planned — write them one after another when you are ready.");
        }

        foreach (var (added, chapter) in candidates)
        {
            var name = chapter.Title.Trim().Length > 0 ? $"Chapter {chapter.Number}: {chapter.Title}" : $"Chapter {chapter.Number}";
            if (chapter.Content.Trim().Length == 0)
            {
                if (chapter.SectionId is int sid && sectionCards.Contains(sid)) continue;
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

    private static int TargetIdOrZero(ChatProposal p, string prefix) =>
        p.Target is { } t && t.StartsWith(prefix) && int.TryParse(t[prefix.Length..], out var id) ? id : 0;

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
                var change = a.SectionId is int sectionId
                    ? await content.AddChapterAsync(bookId, a.Changes, sectionId)
                    : a.AtStart || a.AfterChapterId is not null
                        ? await content.InsertChapterAsync(bookId, a.AtStart ? null : a.AfterChapterId, a.Changes)
                        : await content.AddChapterAsync(bookId, a.Changes);
                p.Target = BookContentService.ChapterTarget(change.ChapterId!.Value);
                return (change, null);
            }
            case BookChatActions.AddSection:
            {
                var change = await content.AddSectionAsync(bookId, a.AtStart ? null : a.AfterSectionId, a.AtStart, a.Changes);
                p.Target = BookContentService.SectionTarget(change.SectionId!.Value);
                return (change, null);
            }
            case BookChatActions.UpdateSectionPlan:
                return (await content.UpdateSectionPlanAsync(bookId, a.SectionId!.Value, a.Changes), null);
            case BookChatActions.MoveSection:
            {
                var sections = (await repo.GetSectionsAsync(bookId)).ToList();
                var index = sections.FindIndex(s => s.Id == a.SectionId);
                if (index < 0) throw new InvalidOperationException("The section no longer exists.");
                await EnsureNothingWrittenAsync(bookId, sections[index], "moved");
                // Where it was, for undo.
                var previous = index > 0 ? sections[index - 1] : null;
                p.PayloadJson = (a with { UndoAfterSectionId = previous?.Id, UndoAtStart = previous is null }).ToJson();
                return (await content.MoveSectionAsync(bookId, a.SectionId!.Value, a.AtStart ? null : a.AfterSectionId), null);
            }
            case BookChatActions.ArchiveSection:
            {
                var section = await repo.GetSectionAsync(bookId, a.SectionId!.Value)
                    ?? throw new InvalidOperationException("The section no longer exists.");
                await EnsureNothingWrittenAsync(bookId, section, "removed");
                return (await content.ArchiveSectionAsync(bookId, section.Id), null);
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
            case BookChatActions.BreakDownSection:
                return (await BreakDownStepAsync(bookId, a.SectionId ?? await AddedSectionIdAsync(bookId, p),
                    BookContentRender.Number(a.Changes.GetValueOrDefault(BookContentRender.PlannedChaptersField)), ct), null);
            default:   // write_section is expanded by the batch, never run on its own
                throw new InvalidOperationException($"Unknown action \"{a.Type}\".");
        }
    }

    /// <summary>Refuses to move or remove a section once one of its chapters holds prose: written chapters never move.</summary>
    private async Task EnsureNothingWrittenAsync(int bookId, Section section, string verb)
    {
        if ((await repo.GetChaptersAsync(bookId)).FirstOrDefault(c => c.SectionId == section.Id && c.Content.Trim().Length > 0) is { } written)
            throw new InvalidOperationException(
                $"Chapter {written.Number} of Section {section.Number} has been written — only sections with nothing written can be {verb}.");
    }

    /// <summary>
    /// The chapter a write or outline check needs, added by the proposal it depends on: an added chapter, or the one
    /// chapter of an added section.
    /// </summary>
    private async Task<int> AddedChapterIdAsync(int bookId, ChatProposal p)
    {
        var added = p.DependsOnProposalId is int id ? await repo.GetProposalAsync(bookId, id) : null;
        if (added?.Target is { } t && t.StartsWith("chapter:") && int.TryParse(t[8..], out var chapterId)) return chapterId;
        if (added is not null && TargetIdOrZero(added, "section:") is var sectionId and > 0
            && (await repo.GetChaptersAsync(bookId)).FirstOrDefault(c => c.SectionId == sectionId) is { } first)
            return first.Id;
        throw new InvalidOperationException("The chapter to write was not added.");
    }

    /// <summary>The section a breakdown needs, added by the proposal it depends on.</summary>
    private async Task<int> AddedSectionIdAsync(int bookId, ChatProposal p)
    {
        var added = p.DependsOnProposalId is int id ? await repo.GetProposalAsync(bookId, id) : null;
        return added is not null && TargetIdOrZero(added, "section:") is var sectionId and > 0
            ? sectionId
            : throw new InvalidOperationException("The section to break down was not added.");
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

    /// <summary>
    /// The break_down_section step: the planner divides the section into chapters (the same LLM call as the Chapters
    /// page's Break down) and the result is posted as proposals instead of being saved — add_chapter for the new
    /// chapters at the end of the section, update_chapter_outline for the chapters it has, archive_chapter for the ones
    /// it no longer needs. For a section of one chapter, that chapter's new outline depends on the first added
    /// chapter: once the section has two chapters it keeps its own overall plan instead of following the first one.
    /// </summary>
    private async Task<ContentChange> BreakDownStepAsync(int bookId, int sectionId, int? chapterCount, CancellationToken ct)
    {
        var section = await repo.GetSectionAsync(bookId, sectionId) ?? throw new InvalidOperationException("The section no longer exists.");
        if (section.IsArchived) throw new InvalidOperationException($"Section {section.Number} is archived.");
        var count = chapterCount ?? (section.PlannedChapters > 1 ? Math.Min(section.PlannedChapters, BookChatActions.MaxSectionChapters) : null);

        state.UpdateRunRole(bookId, AgentRole.ChaptersAgent, null);
        var plan = await planner.PlanBreakdownAsync(bookId, sectionId, count, ct);
        var parts = plan.Parts.Take(BookChatActions.MaxSectionChapters).ToList();
        var current = plan.Current;
        var n = plan.Section.Number;
        var name = $"Section {n} \"{plan.Section.Title}\"";
        if (parts.Count < 2)
        {
            await chat.ProposeAsync(bookId,
                $"⋔ The breakdown of {name} came back with {parts.Count} chapter(s) — nothing to propose. Run it again, or ask me to plan its chapters.", []);
            return new ContentChange("The breakdown came back with fewer than two chapters.", "", "");
        }

        static List<KeyValuePair<string, string>> Plan(Chapter part) =>
            BookContentRender.Fields(part).Where(kv => kv.Key != "number").ToList();
        var actions = new List<RawChatAction>();
        for (var i = current.Count; i < parts.Count; i++)
            actions.Add(Raw(BookChatActions.AddChapter, $"Add chapter {i + 1} of {parts.Count} to Section {n}: {parts[i].Title}",
                section: n, changes: Plan(parts[i])));
        for (var i = 0; i < current.Count && i < parts.Count; i++)
            actions.Add(Raw(BookChatActions.UpdateChapterOutline, $"Make Chapter {current[i].Number} chapter {i + 1} of {parts.Count}: {parts[i].Title}",
                chapter: current[i].Number, changes: Plan(parts[i])));
        foreach (var extra in current.Skip(parts.Count))
            actions.Add(Raw(BookChatActions.ArchiveChapter, $"Remove Chapter {extra.Number} \"{extra.Title}\" — the breakdown has no place for it",
                chapter: extra.Number));

        var list = string.Join("\n", parts.Select((c, i) => $"{i + 1}. **{c.Title}** — {c.Outline}"));
        var message = await chat.ProposeAsync(bookId,
            $"⋔ The planner divided {name} into {parts.Count} chapters:\n\n{list}\n\nApply them together (Apply all)" +
            (current.Count == 1 ? " — the first chapter's new outline is applied after a chapter is added, so the section keeps its overall plan." : "."),
            actions, maxActions: actions.Count);

        if (current.Count == 1
            && message.Proposals.Where(p => p.Type == BookChatActions.AddChapter).MinBy(p => p.Ordinal) is { } firstAdd
            && message.Proposals.FirstOrDefault(p => p.Type == BookChatActions.UpdateChapterOutline) is { } firstOutline)
        {
            firstOutline.DependsOnProposalId = firstAdd.Id;
            await repo.UpdateProposalAsync(firstOutline);
        }
        return new ContentChange($"Divided into {parts.Count} chapters — apply them below.", "", "");
    }

    private static RawChatAction Raw(string type, string label, int? chapter = null, string? name = null,
        List<KeyValuePair<string, string>>? changes = null, string? instructions = null,
        string? originalText = null, string? replacementText = null, int? section = null) =>
        new(type, label.Length > 300 ? label[..300] + "…" : label, chapter, name, changes ?? [], instructions, originalText, replacementText,
            null, null, section);

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
                BookChatActions.AddSection => await content.ArchiveSectionAsync(bookId, TargetId(p, "section:")),
                BookChatActions.UpdateSectionPlan => await content.UpdateSectionPlanAsync(bookId, a.SectionId!.Value,
                    BookContentRender.Parse(before).Where(kv => kv.Key != "number").ToDictionary()),
                BookChatActions.MoveSection => await content.MoveSectionAsync(bookId, a.SectionId!.Value,
                    a.UndoAtStart ? null : a.UndoAfterSectionId),
                BookChatActions.ArchiveSection => await content.RestoreSectionAsync(bookId, a.SectionId!.Value),
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
