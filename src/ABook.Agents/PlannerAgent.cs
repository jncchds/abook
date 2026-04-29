#pragma warning disable SKEXP0001, SKEXP0010, SKEXP0070

using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

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
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.ChaptersAgent, "Running", ct);
        await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Phase 4/4 - Chapter Outlines...", false, ct);

        var (kernel, config) = await GetKernelAsync(bookId);

        var charSumForChapters = string.Join("\n", characters.Select(c =>
            $"- {c.Name} ({c.Role}): {c.GoalMotivation}. Arc: {c.Arc}"));
        var threadSummary = string.Join("\n", threads.Select(t =>
            $"- {t.Name} ({t.Type}, introduced ch.{t.IntroducedChapterNumber?.ToString() ?? "?"}): {t.Description}"));

        var chapterHistory = new ChatHistory();
        var systemPrompt = !string.IsNullOrWhiteSpace(book.ChapterOutlinesSystemPrompt)
            ? InterpolateSystemPrompt(book.ChapterOutlinesSystemPrompt, book, bible)
            : InterpolateSystemPrompt(DefaultPrompts.ChapterOutlines, book, bible);
        chapterHistory.AddSystemMessage(systemPrompt);
        chapterHistory.AddUserMessage($"""
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

            Create {book.TargetChapterCount} detailed chapter outlines for this book.
            """);

        var chapterRaw = await StreamResponseAsync(kernel, config, chapterHistory, bookId, null, AgentRole.ChaptersAgent, ct, jsonMode: true);

        List<Chapter> chapters;
        try { chapters = ParseChapterOutlines(bookId, chapterRaw); }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Book {BookId}] PlannerAgent: could not parse Chapter Outlines JSON.", bookId);
            await ReportErrorAsync(bookId, null, AgentRole.ChaptersAgent, "Chapter Outlines JSON could not be parsed. Try again.", ct);
            throw;
        }

        if (chapters.Count == 0)
            Logger.LogWarning("[Book {BookId}] PlannerAgent parsed zero chapters.", bookId);

        // Clear existing chapters *after* successful parse so a failed re-plan does not wipe the book
        await Repo.DeleteChaptersAsync(bookId);
        foreach (var chapter in chapters)
            await Repo.AddChapterAsync(chapter);
        book.ChaptersStatus = PlanningPhaseStatus.Complete;
        await Repo.UpdateAsync(book);
        await Notifier.NotifyWorkflowProgressAsync(bookId,
            $"Planning: {chapters.Count} chapter{(chapters.Count == 1 ? "" : "s")} outlined. (complete)", false, ct);
        return chapters;
    }

    // JSON Parser

    private static List<Chapter> ParseChapterOutlines(int bookId, string raw)
    {
        var json = ExtractJson(raw, '[', ']');
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var chapters = new List<Chapter>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            string Get(string k) => el.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
            string GetArray(string k)
            {
                if (!el.TryGetProperty(k, out var v)) return "[]";
                if (v.ValueKind == System.Text.Json.JsonValueKind.Array)
                    return v.GetRawText();
                return "[]";
            }
            chapters.Add(new Chapter
            {
                BookId = bookId,
                Number = el.TryGetProperty("number", out var nv) ? nv.GetInt32() : chapters.Count + 1,
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
        return chapters;
    }
}
