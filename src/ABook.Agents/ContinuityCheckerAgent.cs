#pragma warning disable SKEXP0001, SKEXP0010, SKEXP0070

using System.Text.Json;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ABook.Agents;

public class ContinuityCheckerAgent : AgentBase
{
    public ContinuityCheckerAgent(
        IBookRepository repo,
        ILlmProviderFactory llmFactory,
        IVectorStoreService vectorStore,
        IBookNotifier notifier,
        AgentRunStateService stateService,
        ILoggerFactory loggerFactory)
        : base(repo, llmFactory, vectorStore, notifier, stateService, loggerFactory) { }

    /// <summary>
    /// Pre-write two-step check: detects outline contradictions in a single chapter against
    /// established facts (CharacterCards, PlotThreads, prior chapter synopses), then
    /// automatically fixes the outline if issues are found.
    /// Never blocks the workflow â€” always returns so writing can proceed.
    /// </summary>
    public async Task PreWriteCheckAndFixAsync(int bookId, int chapterId, CancellationToken ct = default)
    {
        var book = await Repo.GetByIdAsync(bookId)
            ?? throw new InvalidOperationException($"Book {bookId} not found.");
        var chapter = await Repo.GetChapterAsync(bookId, chapterId)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");

        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.ContinuityChecker, "Running", ct);

        var kernel = await GetKernelAsync(bookId);
        var establishedFacts = await BuildEstablishedFactsAsync(bookId, chapter);

        // â”€â”€ Step 1: Detect â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        var detectHistory = new ChatHistory();
        detectHistory.AddSystemMessage($"""
            You are a story continuity expert. Your task is to evaluate a single chapter outline
            against the established facts of a book and identify any contradictions.
            If the outline is consistent, respond with exactly the text: No issues found.
            Otherwise list each contradiction on a numbered line. Be specific: name the
            established fact it conflicts with and what the outline says instead.
            Book: {book.Title} | Genre: {book.Genre} | Language: {book.Language}
            """);
        detectHistory.AddUserMessage($"""
            ## Established Facts
            {establishedFacts}

            ## Chapter Outline Under Review
            Chapter {chapter.Number}: {chapter.Title}
            Outline: {chapter.Outline}
            {(chapter.PovCharacter.Length > 0 ? $"POV: {chapter.PovCharacter}" : "")}
            {(chapter.ForeshadowingNotes.Length > 0 ? $"Foreshadowing notes: {chapter.ForeshadowingNotes}" : "")}
            {(chapter.PayoffNotes.Length > 0 ? $"Payoff notes: {chapter.PayoffNotes}" : "")}

            Are there any contradictions between this outline and the established facts above?
            """);

        var detectResponse = await StreamResponseAsync(kernel, detectHistory, bookId, chapterId, AgentRole.ContinuityChecker, ct, suspiciousThreshold: 0);

        if (detectResponse.Trim().StartsWith("No issues found", StringComparison.OrdinalIgnoreCase))
        {
            await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.ContinuityChecker, "Done", ct);
            return;
        }

        // â”€â”€ Step 2: Fix â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        var fixHistory = new ChatHistory();
        fixHistory.AddSystemMessage($"""
            You are a story editor. Your task is to rewrite a chapter outline to resolve
            all listed contradictions with the established facts, while preserving the
            chapter's narrative purpose and key events.
            Output ONLY the corrected outline text â€” no headings, no explanations.
            Write in {book.Language}.
            """);
        fixHistory.AddUserMessage($"""
            ## Established Facts
            {establishedFacts}

            ## Original Chapter Outline
            Chapter {chapter.Number}: {chapter.Title}
            {chapter.Outline}

            ## Contradictions to Resolve
            {detectResponse}

            Rewrite the outline to resolve all contradictions above.
            """);

        var fixedOutline = await StreamResponseAsync(kernel, fixHistory, bookId, chapterId, AgentRole.ContinuityChecker, ct);

        if (!string.IsNullOrWhiteSpace(fixedOutline))
        {
            var originalOutline = chapter.Outline;
            chapter.Outline = fixedOutline.Trim();
            await Repo.UpdateChapterAsync(chapter);
            await Notifier.NotifyChapterUpdatedAsync(bookId, chapterId, ct);

            // Persist a SystemNote so the correction is visible in the chat panel
            await Repo.AddMessageAsync(new AgentMessage
            {
                BookId = bookId,
                ChapterId = chapterId,
                AgentRole = AgentRole.ContinuityChecker,
                MessageType = MessageType.SystemNote,
                Content = $"â„¹ï¸ Pre-write review auto-corrected the outline for Chapter {chapter.Number}.\n\n" +
                          $"**Issues found:**\n{detectResponse}\n\n" +
                          $"**Original outline:**\n{originalOutline}",
                IsResolved = true
            });
        }

        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.ContinuityChecker, "Done", ct);
    }

    /// <summary>
    /// Checks continuity. When <paramref name="chapterId"/> is provided, only reports issues
    /// introduced by that chapter against the preceding ones (ignoring pre-existing issues
    /// between earlier chapters). When null, performs a full manuscript review.
    /// Now uses CharacterCards and PlotThreads as structured context alongside RAG.
    /// </summary>
    public async Task<string> CheckAsync(int bookId, int? chapterId = null, CancellationToken ct = default)
    {
        var book = await Repo.GetByIdWithDetailsAsync(bookId)
            ?? throw new InvalidOperationException($"Book {bookId} not found.");

        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.ContinuityChecker, "Running", ct);

        var kernel = await GetKernelAsync(bookId);
        var doneChapters = book.Chapters.Where(c => c.Status == ChapterStatus.Done || c.Status == ChapterStatus.Review).ToList();

        if (doneChapters.Count == 0)
        {
            await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.ContinuityChecker, "Done", ct);
            return string.Empty;
        }

        var currentChapter = chapterId.HasValue
            ? doneChapters.FirstOrDefault(c => c.Id == chapterId.Value)
            : null;
        var previousChapters = currentChapter != null
            ? doneChapters.Where(c => c.Number < currentChapter.Number).ToList()
            : doneChapters;
        var chaptersToSummarise = currentChapter != null
            ? doneChapters.Where(c => c.Number <= currentChapter.Number).ToList()
            : doneChapters;

        var synopsis = string.Join("\n\n", chaptersToSummarise.Select(c =>
            $"Chapter {c.Number}: {c.Title}\nOutline: {c.Outline}\nContent excerpt: {c.Content?[..Math.Min(800, c.Content?.Length ?? 0)]}..."));

        // Structured context from planning artifacts
        var structuredContext = await BuildStructuredContextAsync(bookId);

        // RAG passages
        var config = await Repo.GetLlmConfigAsync(bookId, book.UserId);
        var ragContext = string.Empty;
        if (config != null)
        {
            var ragQueries = new[]
            {
                "character physical description appearance age name backstory",
                "timeline dates past future sequence of events",
                "location place setting description",
            };
            var ragParts = new List<string>();
            foreach (var q in ragQueries)
            {
                var part = await GetRagContextAsync(bookId, q, 4, LlmFactory, config, chapterId, ct);
                if (!string.IsNullOrWhiteSpace(part)) ragParts.Add(part);
            }
            if (ragParts.Count > 0)
                ragContext = string.Join("\n\n===\n\n", ragParts);
        }

        var history = new ChatHistory();

        var bible = await Repo.GetStoryBibleAsync(bookId);

        string systemPrompt;
        if (!string.IsNullOrWhiteSpace(book.ContinuityCheckerSystemPrompt))
        {
            systemPrompt = InterpolateSystemPrompt(book.ContinuityCheckerSystemPrompt, book, bible);
        }
        else if (currentChapter != null)
        {
            systemPrompt = InterpolateSystemPrompt(DefaultPrompts.ContinuityCheckerPerChapter, book, bible) +
                $"\nIMPORTANT: Do NOT report issues that exist solely between previous chapters. " +
                $"Focus only on contradictions introduced by Chapter {currentChapter.Number} (\"{currentChapter.Title}\").";
        }
        else
        {
            systemPrompt = InterpolateSystemPrompt(DefaultPrompts.ContinuityCheckerFull, book, bible);
        }
        history.AddSystemMessage(systemPrompt);

        // Build user message
        var contextSections = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(structuredContext))
            contextSections.AppendLine(structuredContext);
        if (!string.IsNullOrWhiteSpace(ragContext))
        {
            contextSections.AppendLine("\n## Detailed Passages (retrieved for continuity review)");
            contextSections.AppendLine(ragContext);
        }

        string userMessage;
        if (currentChapter != null)
        {
            var prevSynopsis = previousChapters.Count > 0
                ? string.Join("\n\n", previousChapters.Select(c =>
                    $"Chapter {c.Number}: {c.Title}\nOutline: {c.Outline}\nContent excerpt: {c.Content?[..Math.Min(600, c.Content?.Length ?? 0)]}..."))
                : "(No preceding chapters yet.)";

            var currentExcerpt =
                $"Chapter {currentChapter.Number}: {currentChapter.Title}\n" +
                $"Outline: {currentChapter.Outline}\n" +
                $"Content: {currentChapter.Content}";

            userMessage = $"""
                Check Chapter {currentChapter.Number} ("{currentChapter.Title}") for continuity issues.
                Do NOT report problems between the preceding chapters â€” only issues introduced by this chapter.
                {contextSections}
                ## Preceding Chapters (established facts)
                {prevSynopsis}

                ## Chapter Under Review
                {currentExcerpt}
                """;
        }
        else
        {
            userMessage = $"""
                Review these chapters for continuity issues.
                {contextSections}
                ## Chapter Summaries
                {synopsis}
                """;
        }
        history.AddUserMessage(userMessage);

        var response = await StreamResponseAsync(kernel, history, bookId, null, AgentRole.ContinuityChecker, ct, suspiciousThreshold: 0);

        await Repo.AddMessageAsync(new AgentMessage
        {
            BookId = bookId,
            ChapterId = null,
            AgentRole = AgentRole.ContinuityChecker,
            MessageType = MessageType.SystemNote,
            Content = response,
            IsResolved = false
        });

        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.ContinuityChecker, "Done", ct);
        return response;
    }

    // â”€â”€ Private Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Builds a compact "established facts" block for a specific chapter's pre-write check:
    /// relevant CharacterCards + PlotThreads + synopses of prior done chapters.
    /// </summary>
    private async Task<string> BuildEstablishedFactsAsync(int bookId, Chapter chapter)
    {
        var sb = new System.Text.StringBuilder();

        // Story Bible (world rules are canonical)
        var bible = await Repo.GetStoryBibleAsync(bookId);
        if (bible is not null && !string.IsNullOrWhiteSpace(bible.WorldRules))
        {
            sb.AppendLine("### World Rules");
            sb.AppendLine(bible.WorldRules);
        }

        // Character Cards relevant to this chapter
        var allCards = await Repo.GetCharacterCardsAsync(bookId);
        if (allCards.Any())
        {
            List<string> names;
            try { names = JsonSerializer.Deserialize<List<string>>(chapter.CharactersInvolvedJson) ?? []; }
            catch { names = []; }

            var cards = names.Count > 0
                ? allCards.Where(c => names.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList()
                : allCards.ToList();

            if (cards.Count > 0)
            {
                sb.AppendLine("\n### Character Profiles (canonical)");
                foreach (var card in cards)
                {
                    sb.AppendLine($"\n**{card.Name}** ({card.Role})");
                    if (!string.IsNullOrWhiteSpace(card.PhysicalDescription))
                        sb.AppendLine($"  Appearance: {card.PhysicalDescription}");
                    if (!string.IsNullOrWhiteSpace(card.Personality))
                        sb.AppendLine($"  Personality: {card.Personality}");
                    if (!string.IsNullOrWhiteSpace(card.Backstory))
                        sb.AppendLine($"  Backstory: {card.Backstory}");
                    if (!string.IsNullOrWhiteSpace(card.GoalMotivation))
                        sb.AppendLine($"  Goal/motivation: {card.GoalMotivation}");
                    if (!string.IsNullOrWhiteSpace(card.Arc))
                        sb.AppendLine($"  Arc: {card.Arc}");
                }
            }
        }

        // Plot Threads relevant to this chapter
        var allThreads = await Repo.GetPlotThreadsAsync(bookId);
        if (allThreads.Any())
        {
            List<string> threadNames;
            try { threadNames = JsonSerializer.Deserialize<List<string>>(chapter.PlotThreadsJson) ?? []; }
            catch { threadNames = []; }

            var threads = threadNames.Count > 0
                ? allThreads.Where(t => threadNames.Contains(t.Name, StringComparer.OrdinalIgnoreCase)).ToList()
                : allThreads.ToList();

            if (threads.Count > 0)
            {
                sb.AppendLine("\n### Plot Threads");
                foreach (var t in threads)
                {
                    sb.Append($"\n**{t.Name}** ({t.Type}, {t.Status})");
                    if (t.IntroducedChapterNumber.HasValue)
                        sb.Append($" â€” introduced ch.{t.IntroducedChapterNumber}");
                    if (t.ResolvedChapterNumber.HasValue)
                        sb.Append($", resolved ch.{t.ResolvedChapterNumber}");
                    sb.AppendLine();
                    sb.AppendLine($"  {t.Description}");
                }
            }
        }

        // Synopses of done/review chapters before this one
        var chapters = await Repo.GetChaptersAsync(bookId);
        var priorChapters = chapters
            .Where(c => c.Number < chapter.Number
                && (c.Status == ChapterStatus.Done || c.Status == ChapterStatus.Review))
            .OrderBy(c => c.Number)
            .ToList();

        if (priorChapters.Count > 0)
        {
            sb.AppendLine("\n### Prior Chapter Synopses");
            foreach (var c in priorChapters)
                sb.AppendLine($"- Ch.{c.Number} \"{c.Title}\": {c.Outline}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds the full structured context block for the complete CheckAsync (Story Bible + all characters + all threads).
    /// </summary>
    private async Task<string> BuildStructuredContextAsync(int bookId)
    {
        var sb = new System.Text.StringBuilder();

        var bible = await Repo.GetStoryBibleAsync(bookId);
        if (bible is not null)
        {
            sb.AppendLine("## Story Bible");
            if (!string.IsNullOrWhiteSpace(bible.SettingDescription))
                sb.AppendLine($"Setting: {bible.SettingDescription}");
            if (!string.IsNullOrWhiteSpace(bible.WorldRules))
                sb.AppendLine($"World rules: {bible.WorldRules}");
            if (!string.IsNullOrWhiteSpace(bible.Themes))
                sb.AppendLine($"Themes: {bible.Themes}");
        }

        var cards = await Repo.GetCharacterCardsAsync(bookId);
        if (cards.Any())
        {
            sb.AppendLine("\n## Character Profiles (canonical)");
            foreach (var card in cards)
            {
                sb.AppendLine($"\n**{card.Name}** ({card.Role})");
                if (!string.IsNullOrWhiteSpace(card.PhysicalDescription))
                    sb.AppendLine($"  Appearance: {card.PhysicalDescription}");
                if (!string.IsNullOrWhiteSpace(card.Backstory))
                    sb.AppendLine($"  Backstory: {card.Backstory}");
                if (!string.IsNullOrWhiteSpace(card.GoalMotivation))
                    sb.AppendLine($"  Goal/motivation: {card.GoalMotivation}");
                if (!string.IsNullOrWhiteSpace(card.Arc))
                    sb.AppendLine($"  Arc: {card.Arc}");
            }
        }

        var threads = await Repo.GetPlotThreadsAsync(bookId);
        if (threads.Any())
        {
            sb.AppendLine("\n## Plot Threads");
            foreach (var t in threads)
            {
                sb.AppendLine($"\n**{t.Name}** ({t.Type}, {t.Status})");
                sb.AppendLine($"  {t.Description}");
            }
        }

        return sb.ToString();
    }
}