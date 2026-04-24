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
    ///
    /// Each phase first gathers questions via a small non-streaming call, asks the author,
    /// then runs the full generation. Skip flags allow Continue Planning to bypass
    /// phases that are already marked Complete.
    ///
    /// Auto-marks each phase Complete after successful DB save.
    /// </summary>
    public async Task<IReadOnlyList<Chapter>> PlanAsync(
        int bookId,
        bool skipStoryBible = false,
        bool skipCharacters = false,
        bool skipPlotThreads = false,
        bool skipChapters = false,
        CancellationToken ct = default)
    {
        var book = await Repo.GetByIdWithDetailsAsync(bookId)
            ?? throw new InvalidOperationException($"Book {bookId} not found.");

        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.Planner, "Running", ct);
        var kernel = await GetKernelAsync(bookId);

        // When any phases are skipped, pre-load previously persisted Q&A so context is preserved
        var qaContext = (skipStoryBible || skipCharacters || skipPlotThreads || skipChapters)
            ? await LoadExistingQaContextAsync(bookId)
            : new StringBuilder();

        // Phase 1: Story Bible
        StoryBible bible;

        if (skipStoryBible)
        {
            await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Skipping Story Bible (marked complete).", false, ct);
            bible = await Repo.GetStoryBibleAsync(bookId)
                ?? throw new InvalidOperationException("Story Bible is marked complete but was not found in DB.");
        }
        else
        {
            await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Phase 1/4 - Story Bible...", false, ct);

            var bibleContext = $"Book title: {book.Title}\nGenre: {book.Genre}\nPremise: {book.Premise}\nTarget chapter count: {book.TargetChapterCount}";
            var bibleQuestions = await GatherPhaseQuestionsAsync("Story Bible", bibleContext, kernel, bookId, ct);
            if (bibleQuestions.Count > 0)
                await AskQuestionsAsync(bookId, bibleQuestions, qaContext, ct);

            await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Generating Story Bible...", false, ct);
            var bibleHistory = new ChatHistory();
            bibleHistory.AddSystemMessage($"""
                You are a world-building expert. Your task is to create a Story Bible for a book project.
                Output a JSON object with these fields:
                  "settingDescription" (string), "timePeriod" (string), "themes" (string, comma-separated list),
                  "toneAndStyle" (string), "worldRules" (string), "notes" (string).
                Output ONLY the JSON object, no additional text or questions.
                Write all content in {book.Language}.
                """);
            bibleHistory.AddUserMessage($"""
                Book title: {book.Title}
                Genre: {book.Genre}
                Premise: {book.Premise}
                Target chapter count: {book.TargetChapterCount}
                {(qaContext.Length > 0 ? $"\nAuthor guidance:\n{qaContext}" : "")}

                Create the Story Bible for this book.
                """);

            var bibleRaw = await StreamResponseAsync(kernel, bibleHistory, bookId, null, AgentRole.Planner, ct);

            try { bible = ParseStoryBible(bookId, bibleRaw); }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[Book {BookId}] Planner Phase 1: could not parse Story Bible JSON.", bookId);
                await ReportErrorAsync(bookId, null, AgentRole.Planner,
                    "Story Bible JSON could not be parsed. Try again or simplify the premise.", ct);
                throw;
            }
            await Repo.UpsertStoryBibleAsync(bible);
            book.StoryBibleStatus = PlanningPhaseStatus.Complete;
            await Repo.UpdateAsync(book);
            await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Story Bible saved. (complete)", false, ct);
        }

        // Phase 2: Character Cards
        List<CharacterCard> characters;

        if (skipCharacters)
        {
            await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Skipping Characters (marked complete).", false, ct);
            characters = (await Repo.GetCharacterCardsAsync(bookId)).ToList();
        }
        else
        {
            await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Phase 2/4 - Characters...", false, ct);

            var charCtx = $"Book title: {book.Title}\nGenre: {book.Genre}\nPremise: {book.Premise}\nStory Bible Setting: {bible.SettingDescription}\nThemes: {bible.Themes}";
            var charQuestions = await GatherPhaseQuestionsAsync("Characters", charCtx, kernel, bookId, ct);
            if (charQuestions.Count > 0)
                await AskQuestionsAsync(bookId, charQuestions, qaContext, ct);

            await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Generating Characters...", false, ct);
            var charHistory = new ChatHistory();
            charHistory.AddSystemMessage($"""
                You are a character development expert. Your task is to create character profiles for the main
                and significant supporting characters in a book.
                Output a JSON array where each element has:
                  "name" (string), "role" ("Protagonist"|"Antagonist"|"Supporting"|"Minor"),
                  "physicalDescription" (string), "personality" (string), "backstory" (string),
                  "goalMotivation" (string), "arc" (string - how the character changes over the story),
                  "firstAppearanceChapterNumber" (int or null), "notes" (string).
                Include all characters necessary to tell the story. Be thorough.
                Output ONLY the JSON array, no additional text or questions.
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

            try { characters = ParseCharacterCards(bookId, charRaw); }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[Book {BookId}] Planner Phase 2: could not parse Character Cards JSON.", bookId);
                await ReportErrorAsync(bookId, null, AgentRole.Planner, "Character Cards JSON could not be parsed. Try again.", ct);
                throw;
            }
            await Repo.DeleteCharacterCardsAsync(bookId);
            foreach (var card in characters)
                await Repo.AddCharacterCardAsync(card);
            book.CharactersStatus = PlanningPhaseStatus.Complete;
            await Repo.UpdateAsync(book);
            await Notifier.NotifyWorkflowProgressAsync(bookId,
                $"Planning: {characters.Count} character{(characters.Count == 1 ? "" : "s")} saved. (complete)", false, ct);
        }

        // Phase 3: Plot Threads
        List<PlotThread> threads;

        if (skipPlotThreads)
        {
            await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Skipping Plot Threads (marked complete).", false, ct);
            threads = (await Repo.GetPlotThreadsAsync(bookId)).ToList();
        }
        else
        {
            await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Phase 3/4 - Plot Threads...", false, ct);

            var characterSummary = string.Join("\n", characters.Select(c =>
                $"- {c.Name} ({c.Role}): {c.GoalMotivation}. Arc: {c.Arc}"));
            var threadCtx = $"Book title: {book.Title}\nGenre: {book.Genre}\nPremise: {book.Premise}\nCharacters:\n{characterSummary}";
            var threadQuestions = await GatherPhaseQuestionsAsync("Plot Threads", threadCtx, kernel, bookId, ct);
            if (threadQuestions.Count > 0)
                await AskQuestionsAsync(bookId, threadQuestions, qaContext, ct);

            await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Generating Plot Threads...", false, ct);
            var threadHistory = new ChatHistory();
            threadHistory.AddSystemMessage($"""
                You are a story structure expert. Your task is to map all major and minor plot threads
                for a book, including foreshadowing and payoff relationships.
                Output a JSON array where each element has:
                  "name" (string), "description" (string - what this thread is about and why it matters),
                  "type" ("MainPlot"|"Subplot"|"CharacterArc"|"Mystery"|"Foreshadowing"|"WorldBuilding"|"ThematicThread"),
                  "introducedChapterNumber" (int or null), "resolvedChapterNumber" (int or null),
                  "status" ("Active"|"Resolved"|"Dormant").
                Map every significant thread, including foreshadowing seeds that will pay off later.
                Output ONLY the JSON array, no additional text or questions.
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

            try { threads = ParsePlotThreads(bookId, threadRaw); }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[Book {BookId}] Planner Phase 3: could not parse Plot Threads JSON.", bookId);
                await ReportErrorAsync(bookId, null, AgentRole.Planner, "Plot Threads JSON could not be parsed. Try again.", ct);
                throw;
            }
            await Repo.DeletePlotThreadsAsync(bookId);
            foreach (var thread in threads)
                await Repo.AddPlotThreadAsync(thread);
            book.PlotThreadsStatus = PlanningPhaseStatus.Complete;
            await Repo.UpdateAsync(book);
            await Notifier.NotifyWorkflowProgressAsync(bookId,
                $"Planning: {threads.Count} plot thread{(threads.Count == 1 ? "" : "s")} saved. (complete)", false, ct);
        }

        // Phase 4: Chapter Outlines
        List<Chapter> chapters;

        if (skipChapters)
        {
            await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Skipping Chapter Outlines (marked complete).", false, ct);
            chapters = (await Repo.GetChaptersAsync(bookId)).ToList();
        }
        else
        {
            await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Phase 4/4 - Chapter Outlines...", false, ct);

            var charSumForChapters = string.Join("\n", characters.Select(c =>
                $"- {c.Name} ({c.Role}): {c.GoalMotivation}. Arc: {c.Arc}"));
            var threadSummary = string.Join("\n", threads.Select(t =>
                $"- {t.Name} ({t.Type}, introduced ch.{t.IntroducedChapterNumber?.ToString() ?? "?"}): {t.Description}"));

            var chapterCtx = $"Book title: {book.Title}\nGenre: {book.Genre}\nPremise: {book.Premise}\nTarget chapters: {book.TargetChapterCount}\nCharacters: {charSumForChapters}";
            var chapterQuestions = await GatherPhaseQuestionsAsync("Chapter Outlines", chapterCtx, kernel, bookId, ct);
            if (chapterQuestions.Count > 0)
                await AskQuestionsAsync(bookId, chapterQuestions, qaContext, ct);

            await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Generating Chapter Outlines...", false, ct);
            var chapterHistory = new ChatHistory();
            var systemPrompt = !string.IsNullOrWhiteSpace(book.PlannerSystemPrompt)
                ? InterpolateSystemPrompt(book.PlannerSystemPrompt, book)
                : $"""
                You are a creative writing Planner. Your task is to outline each chapter of a book in detail.
                Output a JSON array where each element has:
                  "number" (int), "title" (string),
                  "outline" (string - 3-6 sentence synopsis including key events and decisions),
                  "povCharacter" (string - whose point of view this chapter is written from),
                  "charactersInvolved" (array of strings - names of all characters appearing),
                  "plotThreads" (array of strings - names of plot threads active in this chapter),
                  "foreshadowingNotes" (string - any seeds to plant that pay off later; empty string if none),
                  "payoffNotes" (string - any earlier foreshadowing being paid off; empty string if none).
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
                {charSumForChapters}

                Plot Threads:
                {threadSummary}
                {(qaContext.Length > 0 ? $"\nAuthor guidance:\n{qaContext}" : "")}

                Create {book.TargetChapterCount} detailed chapter outlines for this book.
                """);

            var chapterRaw = await StreamResponseAsync(kernel, chapterHistory, bookId, null, AgentRole.Planner, ct);

            try { chapters = ParseChapterOutlines(bookId, chapterRaw); }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[Book {BookId}] Planner Phase 4: could not parse Chapter Outlines JSON.", bookId);
                await ReportErrorAsync(bookId, null, AgentRole.Planner, "Chapter Outlines JSON could not be parsed. Try again.", ct);
                throw;
            }

            if (chapters.Count == 0)
                Logger.LogWarning("[Book {BookId}] Planner parsed zero chapters.", bookId);

            // Clear existing chapters *after* successful parse so a failed re-plan does not wipe the book
            await Repo.DeleteChaptersAsync(bookId);
            foreach (var chapter in chapters)
                await Repo.AddChapterAsync(chapter);
            book.ChaptersStatus = PlanningPhaseStatus.Complete;
            await Repo.UpdateAsync(book);
            await Notifier.NotifyWorkflowProgressAsync(bookId,
                $"Planning: {chapters.Count} chapter{(chapters.Count == 1 ? "" : "s")} outlined. (complete)", false, ct);
        }

        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.Planner, "Done", ct);
        return chapters;
    }

    // Q&A Helpers

    /// <summary>
    /// Makes a small non-streaming LLM call to gather up to 3 questions for the given planning phase.
    /// Returns an empty list if the LLM says nothing is unclear.
    /// </summary>
    private async Task<List<string>> GatherPhaseQuestionsAsync(
        string phaseName, string bookContext, Kernel kernel, int bookId, CancellationToken ct)
    {
        await Notifier.NotifyWorkflowProgressAsync(bookId, $"Planning: Gathering questions for {phaseName}...", false, ct);

        var history = new ChatHistory();
        history.AddSystemMessage($"""
            You are helping plan a book. Given the book context below, identify up to 3 specific questions
            whose answers would meaningfully change the {phaseName} output.
            Output ONLY a numbered list (e.g. "1. Question here?").
            If nothing is unclear, respond with exactly: None.
            """);
        history.AddUserMessage(bookContext);

        string response;
        try { response = await GetCompletionAsync(kernel, history, ct, bookId, null, AgentRole.Planner); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Book {BookId}] Question gathering for {Phase} failed - continuing without questions.", bookId, phaseName);
            return [];
        }

        if (response.Trim().StartsWith("None", StringComparison.OrdinalIgnoreCase))
            return [];

        var questions = new List<string>();
        foreach (var line in response.Split('\n'))
        {
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

    /// <summary>
    /// Loads all persisted Planner Question+Answer messages for a book and reconstructs the Q&A context
    /// string. Used when a partial re-plan is started so prior author answers flow into new phases.
    /// </summary>
    private async Task<StringBuilder> LoadExistingQaContextAsync(int bookId)
    {
        var messages = (await Repo.GetMessagesAsync(bookId))
            .Where(m => m.AgentRole == AgentRole.Planner
                     && (m.MessageType == MessageType.Question || m.MessageType == MessageType.Answer))
            .OrderBy(m => m.CreatedAt)
            .ToList();

        var sb = new StringBuilder();
        for (int i = 0; i < messages.Count - 1; i++)
        {
            var q = messages[i];
            var a = messages[i + 1];
            if (q.MessageType == MessageType.Question && a.MessageType == MessageType.Answer)
            {
                sb.AppendLine($"Q: {q.Content}\nA: {a.Content}\n");
                i++;
            }
        }
        return sb;
    }

    // JSON Parsers

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
