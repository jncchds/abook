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
    /// The LLM may emit <c>[ASK: question]</c> mid-stream to pause and ask the author a clarifying
    /// question; the answer is fed back into the same LLM call so it influences the artifact being
    /// generated. Q&amp;A pairs accumulate in <c>qaContext</c> and are forwarded to every subsequent phase.
    /// When <paramref name="skipCompletedPhases"/> is true, phases whose data already exists in the DB
    /// are skipped (data-presence is treated as the done signal). Used by ContinueWorkflow to fill gaps.
    /// Returns the chapters created or loaded.
    /// </summary>
    public async Task<IReadOnlyList<Chapter>> PlanAsync(int bookId, bool skipCompletedPhases = false, CancellationToken ct = default)
    {
        var book = await Repo.GetByIdWithDetailsAsync(bookId)
            ?? throw new InvalidOperationException($"Book {bookId} not found.");

        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.Planner, "Running", ct);
        var kernel = await GetKernelAsync(bookId);

        // Accumulate Q&A answers across phases to feed forward into subsequent phases
        var qaContext = new StringBuilder();

        // -- Phase 1: Story Bible ----------------------------------------------
        StoryBible bible;
        if (skipCompletedPhases && await Repo.GetStoryBibleAsync(bookId) is { } existingBible)
        {
            bible = existingBible;
            await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Phase 1/4 — Story Bible already exists, skipping.", false, ct);
        }
        else
        {
            await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Phase 1/4 — Story Bible…", false, ct);

            var bibleHistory = new ChatHistory();
            bibleHistory.AddSystemMessage($"""
                You are a world-building expert. Your task is to create a Story Bible for a book project.
                If you need any clarification from the author before generating, emit [ASK: your question]
                on its own line. You may ask up to 3 questions this way. After receiving answers, output
                the Story Bible JSON — do NOT include [ASK: ...] in the final JSON.
                Output a JSON object with these fields:
                  "settingDescription" (string), "timePeriod" (string), "themes" (string, comma-separated list),
                  "toneAndStyle" (string), "worldRules" (string), "notes" (string).
                Write all content in {book.Language}.
                """);
            bibleHistory.AddUserMessage($"""
                Book title: {book.Title}
                Genre: {book.Genre}
                Premise: {book.Premise}
                Target chapter count: {book.TargetChapterCount}

                Create the Story Bible for this book.
                """);

            var bibleRaw = await StreamWithQuestionsAsync(kernel, bibleHistory, bookId, null, AgentRole.Planner, qaContext, ct);

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
        }

        // -- Phase 2: Character Cards ------------------------------------------
        List<CharacterCard> characters;
        if (skipCompletedPhases && (await Repo.GetCharacterCardsAsync(bookId)).ToList() is { Count: > 0 } existingChars)
        {
            characters = existingChars;
            await Notifier.NotifyWorkflowProgressAsync(bookId, $"Planning: Phase 2/4 — {characters.Count} characters already exist, skipping.", false, ct);
        }
        else
        {
            await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Phase 2/4 — Characters…", false, ct);

            var charHistory = new ChatHistory();
            charHistory.AddSystemMessage($"""
                You are a character development expert. Your task is to create character profiles for the main
                and significant supporting characters in a book.
                If you need any clarification from the author before generating, emit [ASK: your question]
                on its own line. You may ask up to 3 questions this way. After receiving answers, output
                the character JSON — do NOT include [ASK: ...] in the final JSON array.
                Output a JSON array where each element has:
                  "name" (string), "role" ("Protagonist"|"Antagonist"|"Supporting"|"Minor"),
                  "physicalDescription" (string), "personality" (string), "backstory" (string),
                  "goalMotivation" (string), "arc" (string — how the character changes over the story),
                  "firstAppearanceChapterNumber" (int or null), "notes" (string).
                Include all characters necessary to tell the story. Be thorough.
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

            var charRaw = await StreamWithQuestionsAsync(kernel, charHistory, bookId, null, AgentRole.Planner, qaContext, ct);

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
        }

        // -- Phase 3: Plot Threads ---------------------------------------------
        var characterSummary = string.Join("\n", characters.Select(c =>
            $"- {c.Name} ({c.Role}): {c.GoalMotivation}. Arc: {c.Arc}"));

        List<PlotThread> threads;
        if (skipCompletedPhases && (await Repo.GetPlotThreadsAsync(bookId)).ToList() is { Count: > 0 } existingThreads)
        {
            threads = existingThreads;
            await Notifier.NotifyWorkflowProgressAsync(bookId, $"Planning: Phase 3/4 — {threads.Count} plot threads already exist, skipping.", false, ct);
        }
        else
        {
            await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Phase 3/4 — Plot Threads…", false, ct);

            var threadHistory = new ChatHistory();
            threadHistory.AddSystemMessage($"""
                You are a story structure expert. Your task is to map all major and minor plot threads
                for a book, including foreshadowing and payoff relationships.
                If you need any clarification from the author before generating, emit [ASK: your question]
                on its own line. You may ask up to 3 questions this way. After receiving answers, output
                the plot thread JSON — do NOT include [ASK: ...] in the final JSON array.
                Output a JSON array where each element has:
                  "name" (string), "description" (string — what this thread is about and why it matters),
                  "type" ("MainPlot"|"Subplot"|"CharacterArc"|"Mystery"|"Foreshadowing"|"WorldBuilding"|"ThematicThread"),
                  "introducedChapterNumber" (int or null), "resolvedChapterNumber" (int or null),
                  "status" ("Active"|"Resolved"|"Dormant").
                Map every significant thread, including foreshadowing seeds that will pay off later.
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

            var threadRaw = await StreamWithQuestionsAsync(kernel, threadHistory, bookId, null, AgentRole.Planner, qaContext, ct);

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
        }

        // -- Phase 4: Chapter Outlines -----------------------------------------
        await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Phase 4/4 — Chapter Outlines…", false, ct);

        // When skipping completed phases, return existing chapters if they already exist.
        if (skipCompletedPhases)
        {
            var existingChapters = (await Repo.GetChaptersAsync(bookId)).ToList();
            if (existingChapters.Count > 0)
            {
                await Notifier.NotifyWorkflowProgressAsync(bookId, $"Planning: {existingChapters.Count} chapters already exist, skipping outline generation.", false, ct);
                await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.Planner, "Done", ct);
                return existingChapters;
            }
        }

        var threadSummary = string.Join("\n", threads.Select(t =>
            $"- {t.Name} ({t.Type}, introduced ch.{t.IntroducedChapterNumber?.ToString() ?? "?"}): {t.Description}"));

        var chapterHistory = new ChatHistory();
        var systemPrompt = !string.IsNullOrWhiteSpace(book.PlannerSystemPrompt)
            ? InterpolateSystemPrompt(book.PlannerSystemPrompt, book)
            : $"""
            You are a creative writing Planner. Your task is to outline each chapter of a book in detail.
            If you need any clarification from the author before generating, emit [ASK: your question]
            on its own line. You may ask up to 3 questions this way. After receiving answers, output
            the chapter outlines JSON — do NOT include [ASK: ...] in the JSON array.
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

        var chapterRaw = await StreamWithQuestionsAsync(kernel, chapterHistory, bookId, null, AgentRole.Planner, qaContext, ct);

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
