using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.Logging;

namespace ABook.Agents;

/// <summary>Planning Phase 4: generates and persists Chapter Outlines. Also owns Q&amp;A helpers used by AgentOrchestrator.</summary>
public class PlannerAgent : AgentBase
{
    public PlannerAgent(
        IBookRepository repo,
        ILlmProviderFactory llmFactory,
        IVectorStoreService vectorStore,
        IBookNotifier notifier,
        AgentRunStateService stateService,
        ILoggerFactory loggerFactory)
        : base(repo, llmFactory, vectorStore, notifier, stateService, loggerFactory) { }

    /// <summary>
    /// Phase 4: generates chapter outlines from the completed Story Bible, Characters, and Plot Threads,
    /// persists them, marks the phase Complete, and returns the saved chapters.
    /// Any outlines already saved are fed back to the model so a re-run refines and extends them instead
    /// of starting over. If the call fails part-way through streaming, the outlines that finished
    /// streaming are merged into the existing chapters before the failure is rethrown.
    /// </summary>
    public async Task<IReadOnlyList<Chapter>> RunAsync(
        Book book,
        StoryBible bible,
        IReadOnlyList<CharacterCard> characters,
        IReadOnlyList<PlotThread> threads,
        CancellationToken ct = default)
    {
        var bookId = book.Id;
        await ReportRunningAsync(bookId, AgentRole.ChaptersAgent, null, ct);
        await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Phase 4/4 - Chapter Outlines...", false, ct);

        var (client, config) = await GetChatClientAsync(bookId);
        var ancestorReference = await BuildAncestorReferenceAsync(bookId, ct);

        // Archived sections and chapters are excluded — they must never reach the model or be overwritten.
        var existing = await Repo.GetSectionsAsync(bookId);
        var existingChapters = (await Repo.GetChaptersAsync(bookId)).ToList();


        var messages = new List<LlmChatMessage>();
        var systemPrompt = !string.IsNullOrWhiteSpace(book.ChapterOutlinesSystemPrompt)
            ? InterpolateSystemPrompt(book.ChapterOutlinesSystemPrompt, book, bible)
            : InterpolateSystemPrompt(DefaultPrompts.ChapterOutlines, book, bible);
        messages.Add(new LlmChatMessage(LlmChatRole.System, systemPrompt));
        messages.Add(new LlmChatMessage(LlmChatRole.User,
            PromptTemplates.ChapterOutlinesUser(book, bible, characters, threads, ancestorReference, BuildExistingBlock(existing, existingChapters))));

        var (chapterRaw, failure) = await StreamJsonResponseAsync(
            client, config, messages, bookId, null, AgentRole.ChaptersAgent, JsonSchemas.ChapterOutlines, ct);

        List<Section> chapters = [];
        List<string> skipped = [];
        Exception? parseError = null;
        try { chapters = ParseSectionOutlines(bookId, chapterRaw, skipped); }
        catch (Exception ex)
        {
            parseError = ex;
            Logger.LogError(ex, "[Book {BookId}] PlannerAgent: could not parse Chapter Outlines JSON.", bookId);
        }

        // A stream can also end cleanly on a truncated array — typically the model hitting MaxTokens.
        if (failure is null && parseError is null && !PartialJson.IsComplete(chapterRaw))
        {
            parseError = new FormatException(
                "The response was cut off before the outline list was closed (the model likely hit its token limit).");
            Logger.LogWarning("[Book {BookId}] PlannerAgent: response truncated — salvaged {Count} outline(s).",
                bookId, chapters.Count);
        }

        if (failure is not null || parseError is not null)
        {
            await KeepPartialAsync(bookId, chapters, parseError);
            Rethrow(failure ?? parseError!);
        }

        if (skipped.Count > 0)
        {
            Logger.LogWarning("[Book {BookId}] PlannerAgent: dropped {Count} unusable element(s): {Detail}",
                bookId, skipped.Count, string.Join(" | ", skipped));
            await ReportNoteAsync(bookId, null, AgentRole.ChaptersAgent,
                PlanningParse.KeptSummary(skipped, chapters.Count, "section outline(s)"), ct);
        }

        // Sync existing sections *after* a successful parse so a failed re-plan does not wipe the book.
        // Matching sections keep their chapters, prose and versions; the whole sync runs in one transaction.
        var saved = await Repo.ReplaceSectionsAsync(bookId, chapters);
        book.ChaptersStatus = PlanningPhaseStatus.Complete;
        await Repo.UpdateAsync(book);
        await Notifier.NotifyWorkflowProgressAsync(bookId,
            $"Planning: {chapters.Count} section{(chapters.Count == 1 ? "" : "s")} outlined. (complete)", false, ct);
        return saved;
    }

    /// <summary>
    /// Renders the section outlines already saved for this book so a re-run builds on them. Sections with prose are
    /// flagged so the model knows their outline is committed to. Empty when the book has no sections.
    /// </summary>
    private static string BuildExistingBlock(IReadOnlyList<Section> existing, IReadOnlyList<Chapter> chapters)
    {
        if (existing.Count == 0) return string.Empty;

        var json = SerializeForPrompt(existing.Select(s => new
        {
            number = s.Number,
            title = s.Title,
            outline = s.Outline,
            povCharacter = s.PovCharacter,
            foreshadowingNotes = s.ForeshadowingNotes,
            payoffNotes = s.PayoffNotes,
            chapters = Math.Max(1, chapters.Count(c => c.SectionId == s.Id)),
            alreadyWritten = chapters.Any(c => c.SectionId == s.Id && !string.IsNullOrWhiteSpace(c.Content)),
        }));

        return PromptTemplates.ExistingChapterOutlines(json);
    }

    // ── Section breakdown ───────────────────────────────────────────────────────

    /// <summary>
    /// Divides a section into chapters with one LLM call: the section keeps its overall plan, its first chapter gets
    /// the first chapter's plan, new chapters are added after it, and any further unwritten chapters it had are
    /// archived. Only a section none of whose chapters is written yet can be broken down (a written one is split).
    /// <paramref name="chapterCount"/> null lets the model decide (2-4).
    /// </summary>
    public async Task<IReadOnlyList<Chapter>> BreakDownSectionAsync(int bookId, int sectionId, int? chapterCount, CancellationToken ct)
    {
        var book = await Repo.GetByIdAsync(bookId) ?? throw new InvalidOperationException($"Book {bookId} not found.");
        var section = await Repo.GetSectionAsync(bookId, sectionId)
            ?? throw new InvalidOperationException($"Section {sectionId} not found.");
        if (section.IsArchived) throw new InvalidOperationException($"Section {section.Number} is archived. Restore it first.");
        var current = (await Repo.GetChaptersAsync(bookId)).Where(c => c.SectionId == sectionId).ToList();
        if (current.Any(c => !string.IsNullOrWhiteSpace(c.Content)))
            throw new InvalidOperationException(
                $"Section {section.Number} already has written chapters. Split a chapter instead, or clear their text first.");

        await ReportRunningAsync(bookId, AgentRole.ChaptersAgent, null, ct);
        await Notifier.NotifyWorkflowProgressAsync(bookId, $"Breaking down Section {section.Number} \"{section.Title}\" into chapters…", false, ct);

        var (client, config) = await GetChatClientAsync(bookId);
        var bible = await Repo.GetStoryBibleAsync(bookId);
        var characters = await Repo.GetCharacterCardsAsync(bookId);
        var sections = await Repo.GetSectionsAsync(bookId);
        var messages = new List<LlmChatMessage>
        {
            new(LlmChatRole.System, PromptTemplates.SectionBreakdownSystem),
            new(LlmChatRole.User, PromptTemplates.SectionBreakdownUser(book, bible, characters, sections, section,
                chapterCount, await BuildAncestorReferenceAsync(bookId, ct))),
        };
        var (raw, failure) = await StreamJsonResponseAsync(
            client, config, messages, bookId, null, AgentRole.ChaptersAgent, JsonSchemas.SectionBreakdown, ct, forwardToUi: false);
        if (failure is not null) Rethrow(failure);

        var parts = ParseOutlineItems(bookId, NumberedBreakdown(raw), null).Select(i => i.Chapter).ToList();
        if (chapterCount is int n && parts.Count > n) parts = parts.Take(n).ToList();

        var result = await ApplyBreakdownAsync(bookId, section, current, parts);
        await Notifier.NotifyWorkflowProgressAsync(bookId,
            $"Section {section.Number} now has {result.Count} chapter{(result.Count == 1 ? "" : "s")}.", false, ct);
        return result;
    }

    /// <summary>The breakdown's elements numbered 1..N, so the outline parser reads them like planned sections.</summary>
    private static string NumberedBreakdown(string raw)
    {
        using var doc = PlanningParse.OpenArray(raw, "Section breakdown");
        var items = new List<Dictionary<string, System.Text.Json.JsonElement>>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (el.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            var item = el.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            item["number"] = System.Text.Json.JsonSerializer.SerializeToElement(items.Count + 1);
            items.Add(item);
        }
        return System.Text.Json.JsonSerializer.Serialize(items);
    }

    /// <summary>
    /// Writes a breakdown: new chapters are added first (so the section, now holding several, keeps its own overall
    /// plan), then the existing chapters get the plans in order, and existing chapters beyond the new count are archived.
    /// </summary>
    private async Task<IReadOnlyList<Chapter>> ApplyBreakdownAsync(int bookId, Section section, List<Chapter> current, List<Chapter> parts)
    {
        var last = current.LastOrDefault();
        for (var i = current.Count; i < parts.Count; i++)
        {
            var chapter = new Chapter { BookId = bookId, SectionId = section.Id, Status = ChapterStatus.Outlined };
            chapter.CopyPlanFrom(ToSection(parts[i]));
            last = await Repo.InsertChapterAsync(chapter, last?.Id, sameSection: last is not null);
        }
        for (var i = 0; i < current.Count && i < parts.Count; i++)
        {
            var chapter = (await Repo.GetChapterAsync(bookId, current[i].Id))!;
            chapter.CopyPlanFrom(ToSection(parts[i]));
            chapter.Status = ChapterStatus.Outlined;
            await Repo.UpdateChapterAsync(chapter);
        }
        foreach (var extra in current.Skip(parts.Count))
            await Repo.ArchiveChapterAsync(bookId, extra.Id);

        var fresh = (await Repo.GetSectionAsync(bookId, section.Id))!;
        fresh.PlannedChapters = parts.Count;
        await Repo.UpdateSectionAsync(fresh);
        var chapters = (await Repo.GetChaptersAsync(bookId)).Where(c => c.SectionId == section.Id).ToList();
        foreach (var c in chapters) await Notifier.NotifyChapterUpdatedAsync(bookId, c.Id);
        return chapters;
    }

    private static Section ToSection(Chapter plan)
    {
        var s = new Section();
        s.CopyPlanFrom(plan);
        return s;
    }

    /// <summary>
    /// Keeps the outlines that finished streaming before the run failed. They are merged into the
    /// existing chapters by number — never replacing the set — so nothing the author has already seen
    /// is lost, and the phase is deliberately left incomplete so the next run continues from here.
    /// </summary>
    private async Task KeepPartialAsync(int bookId, IReadOnlyList<Section> salvaged, Exception? parseError)
    {
        if (salvaged.Count == 0)
        {
            await ReportErrorAsync(bookId, null, AgentRole.ChaptersAgent,
                parseError is not null
                    ? $"No chapter outlines could be saved: {parseError.Message} Try again."
                    : "The Chapter Outlines run failed before any complete outline arrived. Nothing was saved — try again.");
            return;
        }

        await Repo.MergeSectionOutlinesAsync(bookId, salvaged);
        var saved = salvaged;

        Logger.LogWarning("[Book {BookId}] PlannerAgent: kept {Count} chapter outline(s) salvaged from the interrupted run.",
            bookId, saved.Count);
        await ReportErrorAsync(bookId, null, AgentRole.ChaptersAgent,
            $"The Chapter Outlines run was interrupted. Kept the {saved.Count} outline{(saved.Count == 1 ? "" : "s")} " +
            "received before the failure — run it again to complete the set." +
            (parseError is not null ? $" Reason: {parseError.Message}" : ""));
        await Notifier.NotifyWorkflowProgressAsync(bookId,
            $"Planning: kept {saved.Count} chapter outline{(saved.Count == 1 ? "" : "s")} from the interrupted run.",
            false, CancellationToken.None);
    }

    // JSON Parser

    /// <param name="skipped">Receives one line per rejected element, naming the reason and the offending JSON.</param>
    internal static List<Chapter> ParseChapterOutlines(int bookId, string raw, List<string>? skipped = null) =>
        ParseOutlineItems(bookId, raw, skipped).Select(i => i.Chapter).ToList();

    /// <summary>
    /// The planned sections: each item's plan (as a chapter, its section's one chapter) with the number of chapters the
    /// planner expects the section to need ("chapters", 1 when missing; at most 6).
    /// </summary>
    internal static List<Section> ParseSectionOutlines(int bookId, string raw, List<string>? skipped = null) =>
        ParseOutlineItems(bookId, raw, skipped).Select(i =>
        {
            var section = new Section { BookId = bookId, Number = i.Chapter.Number, PlannedChapters = i.PlannedChapters };
            section.CopyPlanFrom(i.Chapter);
            return section;
        }).ToList();

    private static List<(Chapter Chapter, int PlannedChapters)> ParseOutlineItems(int bookId, string raw, List<string>? skipped)
    {
        // Salvaging keeps every element that finished streaming and drops only a truncated tail,
        // so a response cut short still yields the outlines the author already saw.
        using var doc = PlanningParse.OpenArray(raw, "Chapter outlines");
        var chapters = new List<(Chapter, int)>();
        var skips = skipped ?? [];
        var index = 0;
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            index++;
            // One malformed outline must not cost the author the whole plan — drop it and say why.
            try
            {
                string Get(string k) => LenientJson.Text(el, k);
                string GetArray(string k)
                {
                    if (el.ValueKind != System.Text.Json.JsonValueKind.Object
                        || !el.TryGetProperty(k, out var v)) return "[]";
                    if (v.ValueKind == System.Text.Json.JsonValueKind.Array)
                        return v.GetRawText();
                    return "[]";
                }
                chapters.Add((new Chapter
                {
                    BookId = bookId,
                    Number = LenientJson.Int(el, "number") ?? chapters.Count + 1,
                    Title = Get("title"),
                    Outline = Get("outline"),
                    PovCharacter = Get("povCharacter"),
                    CharactersInvolvedJson = GetArray("charactersInvolved"),
                    PlotThreadsJson = GetArray("plotThreads"),
                    ForeshadowingNotes = Get("foreshadowingNotes"),
                    PayoffNotes = Get("payoffNotes"),
                    Status = ChapterStatus.Outlined
                }, Math.Clamp(LenientJson.Int(el, "chapters") ?? 1, 1, 6)));
            }
            catch (Exception ex)
            {
                skips.Add(PlanningParse.SkipNote("Chapter outline", index, el, ex));
            }
        }
        // An empty result must not be allowed to wipe the book's existing chapters.
        if (chapters.Count == 0)
            throw new FormatException(
                $"Chapter outlines response contained no usable chapter. {PlanningParse.SkipSummary(skips, index, raw)}");
        return chapters;
    }
}
