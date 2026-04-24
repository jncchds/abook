#pragma warning disable SKEXP0001, SKEXP0010, SKEXP0070

using System.Text;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ABook.Agents;

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
    /// Runs the 4-phase planning pipeline:
    ///   1. Story Bible  2. Character Cards  3. Plot Threads  4. Chapter Outlines
    /// Questions found after phases 1-3 are asked via AskUserAndWaitAsync (up to 3 per phase).
    /// Returns the list of chapters created.
    /// </summary>
    public async Task<IReadOnlyList<Chapter>> PlanAsync(int bookId, CancellationToken ct = default)
    {
        var book = await Repo.GetByIdWithDetailsAsync(bookId)
            ?? throw new InvalidOperationException($"Book {bookId} not found.");

        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.Planner, "Running", ct);
        var kernel = await GetKernelAsync(bookId);

        // Accumulate Q&A answers across phases to feed forward into subsequent phases
        var qaContext = new StringBuilder();

        // -- Phase 1: Story Bible ----------------------------------------------
        await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Phase 1/4 — Story Bible…", false, ct);

        var bibleHistory = new ChatHistory();
        bibleHistory.AddSystemMessage($"""
            You are a world-building expert. Your task is to create a Story Bible for a book project.
            Output a JSON object with these fields:
              "settingDescription" (string), "timePeriod" (string), "themes" (string, comma-separated list),
              "toneAndStyle" (string), "worldRules" (string), "notes" (string).
            After the JSON, you MAY add a "## Questions" section with up to 3 numbered questions
            for the author about ambiguous world-building choices. Only ask questions that would
            meaningfully change the world design. Omit the section if nothing is unclear.
            Write all content in {book.Language}.
            """);
        bibleHistory.AddUserMessage($"""
            Book title: {book.Title}
            Genre: {book.Genre}
            Premise: {book.Premise}
            Target chapter count: {book.TargetChapterCount}

            Create the Story Bible for this book.
            """);

        var bibleRaw = await StreamResponseAsync(kernel, bibleHistory, bookId, null, AgentRole.Planner, ct);
        var bibleQuestions = ParseQuestionsSection(bibleRaw);

        StoryBible bible;
        try { bible = ParseStoryBible(bookId, bibleRaw); }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Book {BookId}] Planner Phase 1: could not parse Story Bible JSON.", bookId);
            await ReportErrorAsync(bookId, null, AgentRole.Planner,
                "Story Bible JSON could not be parsed. Try again or simplify the premise.", ct);
            throw;
        }
        await Repo.UpsertStoryBibleAsync(bible);
        await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Story Bible saved.", false, ct);

        if (bibleQuestions.Count > 0)
            await AskQuestionsAsync(bookId, bibleQuestions, qaContext, ct);

        // -- Phase 2: Character Cards ------------------------------------------
        await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Phase 2/4 — Characters…", false, ct);

        var charHistory = new ChatHistory();
        charHistory.AddSystemMessage($"""
            You are a character development expert. Your task is to create character profiles for the main
            and significant supporting characters in a book.
            Output a JSON array where each element has:
              "name" (string), "role" ("Protagonist"|"Antagonist"|"Supporting"|"Minor"),
              "physicalDescription" (string), "personality" (string), "backstory" (string),
              "goalMotivation" (string), "arc" (string — how the character changes over the story),
              "firstAppearanceChapterNumber" (int or null), "notes" (string).
            Include all characters necessary to tell the story. Be thorough.
            After the JSON array, you MAY add a "## Questions" section with up to 3 numbered questions
            for the author about character choices. Omit the section if nothing is unclear.
            Write all content in {book.Language}.
            """);
        charHistory.AddUserMessage($"""
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
            {(qaContext.Length > 0 ? $"\nAuthor guidance so far:\n{qaContext}" : "")}

            Create detailed character profiles for this book.
            """);

        var charRaw = await StreamResponseAsync(kernel, charHistory, bookId, null, AgentRole.Planner, ct);
        var charQuestions = ParseQuestionsSection(charRaw);

        List<CharacterCard> characters;
        try { characters = ParseCharacterCards(bookId, charRaw); }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Book {BookId}] Planner Phase 2: could not parse Character Cards JSON.", bookId);
            await ReportErrorAsync(bookId, null, AgentRole.Planner,
                "Character Cards JSON could not be parsed. Try again.", ct);
            throw;
        }
        await Repo.DeleteCharacterCardsAsync(bookId);
        foreach (var card in characters)
            await Repo.AddCharacterCardAsync(card);
        await Notifier.NotifyWorkflowProgressAsync(bookId, $"Planning: {characters.Count} character{(characters.Count == 1 ? "" : "s")} saved.", false, ct);

        if (charQuestions.Count > 0)
            await AskQuestionsAsync(bookId, charQuestions, qaContext, ct);

        // -- Phase 3: Plot Threads ---------------------------------------------
        await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Phase 3/4 — Plot Threads…", false, ct);

        var characterSummary = string.Join("\n", characters.Select(c =>
            $"- {c.Name} ({c.Role}): {c.GoalMotivation}. Arc: {c.Arc}"));

        var threadHistory = new ChatHistory();
        threadHistory.AddSystemMessage($"""
            You are a story structure expert. Your task is to map all major and minor plot threads
            for a book, including foreshadowing and payoff relationships.
            Output a JSON array where each element has:
              "name" (string), "description" (string — what this thread is about and why it matters),
              "type" ("MainPlot"|"Subplot"|"CharacterArc"|"Mystery"|"Foreshadowing"|"WorldBuilding"|"ThematicThread"),
              "introducedChapterNumber" (int or null), "resolvedChapterNumber" (int or null),
              "status" ("Active"|"Resolved"|"Dormant").
            Map every significant thread, including foreshadowing seeds that will pay off later.
            After the JSON array, you MAY add a "## Questions" section with up to 3 numbered questions
            about plot structure. Omit if nothing is unclear.
            Write all content in {book.Language}.
            """);
        threadHistory.AddUserMessage($"""
            Book title: {book.Title}
            Genre: {book.Genre}
            Premise: {book.Premise}
            Target chapter count: {book.TargetChapterCount}

            Story Bible:
            Setting: {bible.SettingDescription}
            Time period: {bible.TimePeriod}
            Themes: {bible.Themes}
            World rules: {bible.WorldRules}

            Characters:
            {characterSummary}
            {(qaContext.Length > 0 ? $"\nAuthor guidance so far:\n{qaContext}" : "")}

            Create the plot thread map for this book.
            """);

        var threadRaw = await StreamResponseAsync(kernel, threadHistory, bookId, null, AgentRole.Planner, ct);
        var threadQuestions = ParseQuestionsSection(threadRaw);

        List<PlotThread> threads;
        try { threads = ParsePlotThreads(bookId, threadRaw); }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Book {BookId}] Planner Phase 3: could not parse Plot Threads JSON.", bookId);
            await ReportErrorAsync(bookId, null, AgentRole.Planner,
                "Plot Threads JSON could not be parsed. Try again.", ct);
            throw;
        }
        await Repo.DeletePlotThreadsAsync(bookId);
        foreach (var thread in threads)
            await Repo.AddPlotThreadAsync(thread);
        await Notifier.NotifyWorkflowProgressAsync(bookId, $"Planning: {threads.Count} plot thread{(threads.Count == 1 ? "" : "s")} saved.", false, ct);

        if (threadQuestions.Count > 0)
            await AskQuestionsAsync(bookId, threadQuestions, qaContext, ct);

        // -- Phase 4: Chapter Outlines -----------------------------------------
        await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Phase 4/4 — Chapter Outlines…", false, ct);

        var threadSummary = string.Join("\n", threads.Select(t =>
            $"- {t.Name} ({t.Type}, introduced ch.{t.IntroducedChapterNumber?.ToString() ?? "?"}): {t.Description}"));

        var chapterHistory = new ChatHistory();
        var systemPrompt = !string.IsNullOrWhiteSpace(book.PlannerSystemPrompt)
            ? InterpolateSystemPrompt(book.PlannerSystemPrompt, book)
            : $"""
            You are a creative writing Planner. Your task is to outline each chapter of a book in detail.
            Output a JSON array where each element has:
              "number" (int), "title" (string),
              "outline" (string — 3-6 sentence synopsis including key events and decisions),
              "povCharacter" (string — whose point of view this chapter is written from),
              "charactersInvolved" (array of strings — names of all characters appearing),
              "plotThreads" (array of strings — names of plot threads active in this chapter),
              "foreshadowingNotes" (string — any seeds to plant that pay off later; empty string if none),
              "payoffNotes" (string — any earlier foreshadowing being paid off; empty string if none).
            Output ONLY the JSON array, no additional text.
            Write all content in {book.Language}.
            """;
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
            {characterSummary}

            Plot Threads:
            {threadSummary}
            {(qaContext.Length > 0 ? $"\nAuthor guidance:\n{qaContext}" : "")}

            Create {book.TargetChapterCount} detailed chapter outlines for this book.
            """);

        var chapterRaw = await StreamResponseAsync(kernel, chapterHistory, bookId, null, AgentRole.Planner, ct);

        List<Chapter> chapters;
        try { chapters = ParseChapterOutlines(bookId, chapterRaw); }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Book {BookId}] Planner Phase 4: could not parse Chapter Outlines JSON.", bookId);
            await ReportErrorAsync(bookId, null, AgentRole.Planner,
                "Chapter Outlines JSON could not be parsed. Try again.", ct);
            throw;
        }

        if (chapters.Count == 0)
            Logger.LogWarning("[Book {BookId}] Planner parsed zero chapters.", bookId);

        // Clear existing chapters *after* successful parse so a failed re-plan doesn't wipe the book
        await Repo.DeleteChaptersAsync(bookId);
        foreach (var chapter in chapters)
            await Repo.AddChapterAsync(chapter);

        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.Planner, "Done", ct);
        return chapters;
    }

    // -- Q&A Helpers -----------------------------------------------------------

    /// <summary>Extracts numbered questions after a "## Questions" heading in an LLM response.</summary>
    private static List<string> ParseQuestionsSection(string response)
    {
        var questions = new List<string>();
        var idx = response.IndexOf("## Questions", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return questions;

        var section = response[(idx + "## Questions".Length)..].TrimStart('\r', '\n');
        foreach (var line in section.Split('\n'))
        {
            // Match lines like "1. question text" or "1) question text"
            var trimmed = line.Trim();
            var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"^\d+[.)]\s+(.+)$");
            if (match.Success)
                questions.Add(match.Groups[1].Value.Trim());
            if (questions.Count >= 3) break;
        }
        return questions;
    }

    /// <summary>Asks each question sequentially and appends answers to the shared qa context.</summary>
    private async Task AskQuestionsAsync(
        int bookId, List<string> questions, StringBuilder qaContext, CancellationToken ct)
    {
        foreach (var question in questions)
        {
            var answer = await AskUserAndWaitAsync(bookId, null, AgentRole.Planner, question, ct);
            if (!string.IsNullOrWhiteSpace(answer))
                qaContext.AppendLine($"Q: {question}\nA: {answer}\n");
        }
    }

    // -- JSON Parsers ----------------------------------------------------------

    private static string ExtractJson(string raw, char open, char close)
    {
        raw = raw.Trim();
        if (raw.StartsWith("```")) raw = string.Join('\n', raw.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")));
        var start = raw.IndexOf(open);
        var end = raw.LastIndexOf(close);
        if (start >= 0 && end > start) return raw[start..(end + 1)];
        return raw;
    }

    private static StoryBible ParseStoryBible(int bookId, string raw)
    {
        var json = ExtractJson(raw, '{', '}');
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var r = doc.RootElement;
        string Get(string key) => r.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";
        return new StoryBible
        {
            BookId = bookId,
            SettingDescription = Get("settingDescription"),
            TimePeriod = Get("timePeriod"),
            Themes = Get("themes"),
            ToneAndStyle = Get("toneAndStyle"),
            WorldRules = Get("worldRules"),
            Notes = Get("notes")
        };
    }

    private static List<CharacterCard> ParseCharacterCards(int bookId, string raw)
    {
        var json = ExtractJson(raw, '[', ']');
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var cards = new List<CharacterCard>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            string Get(string k) => el.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
            var roleStr = Get("role");
            var role = Enum.TryParse<CharacterRole>(roleStr, true, out var r) ? r : CharacterRole.Supporting;
            int? firstCh = el.TryGetProperty("firstAppearanceChapterNumber", out var fv)
                && fv.ValueKind == System.Text.Json.JsonValueKind.Number ? fv.GetInt32() : null;
            cards.Add(new CharacterCard
            {
                BookId = bookId,
                Name = Get("name"),
                Role = role,
                PhysicalDescription = Get("physicalDescription"),
                Personality = Get("personality"),
                Backstory = Get("backstory"),
                GoalMotivation = Get("goalMotivation"),
                Arc = Get("arc"),
                FirstAppearanceChapterNumber = firstCh,
                Notes = Get("notes")
            });
        }
        return cards;
    }

    private static List<PlotThread> ParsePlotThreads(int bookId, string raw)
    {
        var json = ExtractJson(raw, '[', ']');
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var threads = new List<PlotThread>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            string Get(string k) => el.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
            var type = Enum.TryParse<PlotThreadType>(Get("type"), true, out var t) ? t : PlotThreadType.Subplot;
            var status = Enum.TryParse<PlotThreadStatus>(Get("status"), true, out var s) ? s : PlotThreadStatus.Active;
            int? introduced = el.TryGetProperty("introducedChapterNumber", out var iv)
                && iv.ValueKind == System.Text.Json.JsonValueKind.Number ? iv.GetInt32() : null;
            int? resolved = el.TryGetProperty("resolvedChapterNumber", out var rv)
                && rv.ValueKind == System.Text.Json.JsonValueKind.Number ? rv.GetInt32() : null;
            threads.Add(new PlotThread
            {
                BookId = bookId,
                Name = Get("name"),
                Description = Get("description"),
                Type = type,
                IntroducedChapterNumber = introduced,
                ResolvedChapterNumber = resolved,
                Status = status
            });
        }
        return threads;
    }

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
