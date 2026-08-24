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
        string qaContext,
        CancellationToken ct = default)
    {
        var bookId = book.Id;
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.ChaptersAgent, "Running", ct: ct);
        await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Phase 4/4 - Chapter Outlines...", false, ct);

        var (client, config) = await GetChatClientAsync(bookId);
        var ancestorReference = await Repo.BuildAncestorPlanningReferenceAsync(bookId, ct);

        // Archived chapters are excluded — they must never reach the model or be overwritten.
        var existing = (await Repo.GetChaptersAsync(bookId)).OrderBy(c => c.Number).ToList();

        var charSumForChapters = string.Join("\n", characters.Select(c =>
            $"- {c.Name} ({c.Role}): {c.GoalMotivation}. Arc: {c.Arc}"));
        var threadSummary = string.Join("\n", threads.Select(t =>
            $"- {t.Name} ({t.Type}, introduced ch.{t.IntroducedChapterNumber?.ToString() ?? "?"}): {t.Description}"));

        var messages = new List<LlmChatMessage>();
        var systemPrompt = !string.IsNullOrWhiteSpace(book.ChapterOutlinesSystemPrompt)
            ? InterpolateSystemPrompt(book.ChapterOutlinesSystemPrompt, book, bible)
            : InterpolateSystemPrompt(DefaultPrompts.ChapterOutlines, book, bible);
        messages.Add(new LlmChatMessage(LlmChatRole.System, systemPrompt));
        messages.Add(new LlmChatMessage(LlmChatRole.User, $"""
            Book title: {book.Title}
            Genre: {book.Genre}
            Premise: {book.Premise}
            Target chapter count: {book.TargetChapterCount}

            Story Bible:
            Setting: {bible.SettingDescription}
            Time period: {bible.TimePeriod}
            Themes: {bible.Themes}
            Tone & style: {bible.ToneAndStyle}
            World rules: {bible.WorldRules}

            Characters:
            {charSumForChapters}

            Plot Threads:
            {threadSummary}
            {(qaContext.Length > 0 ? $"\nAuthor guidance:\n{qaContext}" : "")}
            {(ancestorReference.Length > 0 ? $"\n{ancestorReference}" : "")}
            {BuildExistingBlock(existing)}
            Create {book.TargetChapterCount} detailed chapter outlines for this book.
            """));

        var (chapterRaw, failure) = await StreamJsonResponseAsync(
            client, config, messages, bookId, null, AgentRole.ChaptersAgent, JsonSchemas.ChapterOutlines, ct);

        List<Chapter> chapters = [];
        List<string> skipped = [];
        Exception? parseError = null;
        try { chapters = ParseChapterOutlines(bookId, chapterRaw, skipped); }
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
                PlanningParse.KeptSummary(skipped, chapters.Count, "chapter outline(s)"), ct);
        }

        // Sync existing chapters *after* a successful parse so a failed re-plan does not wipe the book.
        // Matching chapters keep their prose and versions; the whole sync runs in one transaction.
        var saved = await Repo.ReplaceChaptersAsync(bookId, chapters);
        book.ChaptersStatus = PlanningPhaseStatus.Complete;
        await Repo.UpdateAsync(book);
        await Notifier.NotifyWorkflowProgressAsync(bookId,
            $"Planning: {saved.Count} chapter{(saved.Count == 1 ? "" : "s")} outlined. (complete)", false, ct);
        return saved;
    }

    /// <summary>
    /// Renders the outlines already saved for this book so a re-run builds on them. Chapters that
    /// already have prose are flagged so the model knows their outline is committed to.
    /// Empty when the book has no chapters.
    /// </summary>
    private static string BuildExistingBlock(IReadOnlyList<Chapter> existing)
    {
        if (existing.Count == 0) return string.Empty;

        var json = SerializeForPrompt(existing.Select(c => new
        {
            number = c.Number,
            title = c.Title,
            outline = c.Outline,
            povCharacter = c.PovCharacter,
            foreshadowingNotes = c.ForeshadowingNotes,
            payoffNotes = c.PayoffNotes,
            alreadyWritten = !string.IsNullOrWhiteSpace(c.Content),
        }));

        return $"""

            These chapter outlines are already saved for this book:
            {json}

            Return the complete, updated outline set: repeat every chapter you are keeping (unchanged or
            revised, keeping its exact "number" so it can be matched), and add any chapter that is missing.
            Chapters you leave out will be removed. A chapter marked "alreadyWritten": true has its prose
            written — do not change its premise, only correct outright errors in its outline.

            """;
    }

    /// <summary>
    /// Keeps the outlines that finished streaming before the run failed. They are merged into the
    /// existing chapters by number — never replacing the set — so nothing the author has already seen
    /// is lost, and the phase is deliberately left incomplete so the next run continues from here.
    /// </summary>
    private async Task KeepPartialAsync(int bookId, IReadOnlyList<Chapter> salvaged, Exception? parseError)
    {
        if (salvaged.Count == 0)
        {
            await ReportErrorAsync(bookId, null, AgentRole.ChaptersAgent,
                parseError is not null
                    ? $"No chapter outlines could be saved: {parseError.Message} Try again."
                    : "The Chapter Outlines run failed before any complete outline arrived. Nothing was saved — try again.");
            return;
        }

        var saved = await Repo.MergeChapterOutlinesAsync(bookId, salvaged);

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
    internal static List<Chapter> ParseChapterOutlines(int bookId, string raw, List<string>? skipped = null)
    {
        // Salvaging keeps every element that finished streaming and drops only a truncated tail,
        // so a response cut short still yields the outlines the author already saw.
        using var doc = PlanningParse.OpenArray(raw, "Chapter outlines");
        var chapters = new List<Chapter>();
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
                chapters.Add(new Chapter
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
                });
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
