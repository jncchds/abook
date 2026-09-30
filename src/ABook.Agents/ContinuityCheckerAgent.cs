using System.Text.Json;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.Logging;

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
    /// Never blocks the workflow — always returns so writing can proceed.
    /// </summary>
    public async Task PreWriteCheckAndFixAsync(int bookId, int chapterId, CancellationToken ct = default)
    {
        var book = await Repo.GetByIdAsync(bookId)
            ?? throw new InvalidOperationException($"Book {bookId} not found.");
        var chapter = EnsureNotArchived(await Repo.GetChapterAsync(bookId, chapterId)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found."));

        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.ContinuityChecker, RunStates.Running, chapterId, ct);

        var (client, config) = await GetChatClientAsync(bookId);
        var establishedFacts = await BuildEstablishedFactsAsync(bookId, chapter);

        // ── Step 1: Detect ────────────────────────────────────────────────────
        var detectHistory = new List<LlmChatMessage>
        {
            new(LlmChatRole.System, PromptTemplates.PreWriteDetectSystem(book)),
            new(LlmChatRole.User, PromptTemplates.PreWriteDetectUser(establishedFacts, chapter)),
        };

        var detectResponse = await StreamResponseAsync(client, config, detectHistory, bookId, chapterId, AgentRole.ContinuityChecker, ct,
            forwardToUi: false);

        if (detectResponse.Trim().StartsWith(PromptTemplates.PreWriteNoIssuesMarker, StringComparison.OrdinalIgnoreCase))
        {
            await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.ContinuityChecker, RunStates.Done, chapterId, ct);
            return;
        }

        // ── Step 2: Fix ───────────────────────────────────────────────────────
        var fixHistory = new List<LlmChatMessage>
        {
            new(LlmChatRole.System, PromptTemplates.PreWriteFixSystem(book)),
            new(LlmChatRole.User, PromptTemplates.PreWriteFixUser(establishedFacts, chapter, detectResponse)),
        };

        var fixedOutline = await StreamResponseAsync(client, config, fixHistory, bookId, chapterId, AgentRole.ContinuityChecker, ct,
            forwardToUi: false);

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
                Content = $"ℹ️ Pre-write review auto-corrected the outline for Chapter {chapter.Number}.\n\n" +
                          $"**Issues found:**\n{detectResponse}\n\n" +
                          $"**Original outline:**\n{originalOutline}",
                IsResolved = true
            });
        }

        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.ContinuityChecker, RunStates.Done, chapterId, ct);
    }

    /// <summary>
    /// Checks quality (continuity + style). When <paramref name="chapterId"/> is provided, only
    /// reports issues introduced by that chapter. When null, performs a full manuscript review.
    /// Returns a structured <see cref="CheckerResult"/> so the Editor can fix precise issues.
    /// The result is also saved as a formatted SystemNote for the chat panel.
    /// </summary>
    public async Task<CheckerResult> CheckAsync(int bookId, int? chapterId = null, CancellationToken ct = default)
    {
        var book = await Repo.GetByIdWithActiveDetailsAsync(bookId)
            ?? throw new InvalidOperationException($"Book {bookId} not found.");

        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.ContinuityChecker, RunStates.Running, chapterId, ct);

        var (client, config) = await GetChatClientAsync(bookId);
        var doneChapters = book.Chapters.Where(c => c.Status == ChapterStatus.Done || c.Status == ChapterStatus.Review).ToList();

        if (doneChapters.Count == 0)
        {
            await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.ContinuityChecker, RunStates.Done, chapterId, ct);
            return new CheckerResult(false, [], "No chapters to review.");
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

        // Structured context from planning artifacts (also returns the bible to avoid a second fetch)
        var (structuredContext, bible) = await BuildStructuredContextAsync(bookId);

        // RAG passages
        var ragContext = string.Empty;
        {
            var ragParts = new List<string>();
            foreach (var q in RagQueries.Checker)
            {
                var part = await GetRagContextAsync(bookId, q, 4, LlmFactory, config, chapterId, ct);
                if (!string.IsNullOrWhiteSpace(part)) ragParts.Add(part);
            }
            if (ragParts.Count > 0)
                ragContext = PromptTemplates.JoinRagGroups(ragParts);
        }

        string systemPrompt;
        if (!string.IsNullOrWhiteSpace(book.ContinuityCheckerSystemPrompt))
            systemPrompt = InterpolateSystemPrompt(book.ContinuityCheckerSystemPrompt, book, bible);
        else if (currentChapter != null)
            systemPrompt = InterpolateSystemPrompt(DefaultPrompts.ContinuityCheckerPerChapter, book, bible)
                + PromptTemplates.CheckerPerChapterFocus(currentChapter);
        else
            systemPrompt = InterpolateSystemPrompt(DefaultPrompts.ContinuityCheckerFull, book, bible);

        var userMessage = currentChapter != null
            ? PromptTemplates.CheckerChapterUser(currentChapter, previousChapters, structuredContext, ragContext)
            : PromptTemplates.CheckerFullUser(chaptersToSummarise, structuredContext, ragContext);

        var messages = new List<LlmChatMessage>
        {
            new(LlmChatRole.System, systemPrompt),
            new(LlmChatRole.User, userMessage),
        };

        var responseJson = await StreamResponseAsync(client, config, messages, bookId, (int?)null, AgentRole.ContinuityChecker, ct,
            jsonSchema: CheckerResultDto.JsonSchema, forwardToUi: false);

        // Parse structured JSON result; gracefully degrade on parse failure
        CheckerResult result;
        try
        {
            var json = ExtractJson(responseJson, '{', '}');
            var dto = System.Text.Json.JsonSerializer.Deserialize<CheckerResultDto>(json, _jsonOptions);
            var issues = dto?.Issues?.Select(i => new CheckerIssue(
                i.Type ?? "continuity",
                i.Description ?? string.Empty,
                i.ProposedFix ?? string.Empty,
                i.OriginalText ?? string.Empty,
                i.ReplacementText ?? string.Empty,
                i.Position,
                i.Location,
                i.Problem,
                i.CanonicalFact,
                i.SuggestedRewrite)).ToArray() ?? [];
            result = new CheckerResult(dto?.HasIssues ?? false, issues, dto?.Summary ?? string.Empty);
        }
        catch
        {
            Logger.LogWarning("[Book {BookId}] Checker response was not valid JSON — treating as issues found.", bookId);
            result = new CheckerResult(true, [], $"(Could not parse checker output — raw response below)\n\n{responseJson.Trim()}");
        }

        // Persist formatted report so it is readable in the chat panel
        await Repo.AddMessageAsync(new AgentMessage
        {
            BookId = bookId,
            ChapterId = chapterId,
            AgentRole = AgentRole.ContinuityChecker,
            MessageType = MessageType.SystemNote,
            Content = FormatCheckerReport(result),
            IsResolved = false
        });

        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.ContinuityChecker, RunStates.Done, chapterId, ct);
        return result;
    }

    internal static string FormatCheckerReport(CheckerResult result)
    {
        var sb = new System.Text.StringBuilder();
        var types = new[] { "continuity", "grammar", "repetition", "style", "rewrite" };
        var grouped = types.Select(t => (t, items: result.Issues.Where(i => i.Type == t).ToArray())).Where(g => g.items.Length > 0).ToList();

        sb.AppendLine(result.HasIssues ? $"⚠️ Issues found — {result.Issues.Length} total." : "✅ No issues found.");
        if (!string.IsNullOrWhiteSpace(result.Summary))
            sb.AppendLine($"\n{result.Summary}");

        foreach (var (typeName, items) in grouped)
        {
            sb.AppendLine($"\n### {Capitalize(typeName)} ({items.Length} issue(s))");
            for (int n = 0; n < items.Length; n++)
            {
                var issue = items[n];
                if (issue.Type == "rewrite")
                {
                    var locationHint = !string.IsNullOrWhiteSpace(issue.Location)
                        ? $" in {issue.Location}"
                        : string.Empty;
                    sb.AppendLine($"{n + 1}. **Rewrite{locationHint}**: {issue.Description}");
                    if (!string.IsNullOrWhiteSpace(issue.Problem))
                        sb.AppendLine($"   Problem: {issue.Problem}");
                    if (!string.IsNullOrWhiteSpace(issue.CanonicalFact))
                        sb.AppendLine($"   Canonical fact: {issue.CanonicalFact}");
                }
                else
                {
                    var lineHint = issue.Position.HasValue ? $" on line {issue.Position}" : string.Empty;
                    sb.AppendLine($"{n + 1}. **{Capitalize(issue.Type)}{lineHint}**: {issue.Description}");
                    if (!string.IsNullOrWhiteSpace(issue.OriginalText))
                        sb.Append($"   Original: `{EscapeMarkdown(issue.OriginalText)}`\n");
                }
            }
        }

        return sb.ToString().Trim();
    }


    private static readonly System.Text.Json.JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
    };

    /// <summary>DTO for deserializing the ContinuityChecker JSON response.</summary>
    private sealed class CheckerIssueDto
    {
        public string Type { get; set; } = "continuity";
        public string Description { get; set; } = string.Empty;
        public string ProposedFix { get; set; } = string.Empty;
        public string OriginalText { get; set; } = string.Empty;
        public string ReplacementText { get; set; } = string.Empty;
        public int? Position { get; set; }
        public string? Location { get; set; }
        public string? Problem { get; set; }
        public string? CanonicalFact { get; set; }
        public string? SuggestedRewrite { get; set; }
    }

    /// <summary>DTO for deserializing the ContinuityChecker JSON response.</summary>
    private sealed class CheckerResultDto
    {
        public bool HasIssues { get; set; }
        public CheckerIssueDto[] Issues { get; set; } = [];
        public string Summary { get; set; } = string.Empty;

        public static readonly string JsonSchema = """
            {
              "type": "object",
              "properties": {
                "hasIssues": { "type": "boolean" },
                "issues": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "type": { "type": "string", "enum": ["continuity", "grammar", "repetition", "style", "rewrite"] },
                      "description": { "type": "string" },
                      "proposedFix": { "type": "string" },
                      "originalText": { "type": "string" },
                      "replacementText": { "type": "string" },
                      "position": { "type": ["integer", "null"], "minimum": 1 },
                      "location": { "type": ["string", "null"] },
                      "problem": { "type": ["string", "null"] },
                      "canonicalFact": { "type": ["string", "null"] },
                      "suggestedRewrite": { "type": ["string", "null"] }
                    },
                    "required": ["type", "description", "proposedFix", "originalText", "replacementText"],
                    "additionalProperties": false
                  }
                },
                "summary": { "type": "string" }
              },
              "required": ["hasIssues", "issues", "summary"],
              "additionalProperties": false
            }
            """;
    }


    // ── Private Helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds a compact "established facts" block for a specific chapter's pre-write check:
    /// relevant CharacterCards + PlotThreads + synopses of prior done chapters.
    /// </summary>
    private async Task<string> BuildEstablishedFactsAsync(int bookId, Chapter chapter)
    {
        var bible = await Repo.GetStoryBibleAsync(bookId);

        var allCards = await Repo.GetCharacterCardsAsync(bookId);
        var names = ParseNameList(chapter.CharactersInvolvedJson);
        var cards = names.Count > 0
            ? allCards.Where(c => names.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList()
            : allCards.ToList();

        var allThreads = await Repo.GetPlotThreadsAsync(bookId);
        var threadNames = ParseNameList(chapter.PlotThreadsJson);
        var threads = threadNames.Count > 0
            ? allThreads.Where(t => threadNames.Contains(t.Name, StringComparer.OrdinalIgnoreCase)).ToList()
            : allThreads.ToList();

        // Synopses of done/review chapters before this one
        var priorChapters = (await Repo.GetChaptersAsync(bookId))
            .Where(c => c.Number < chapter.Number
                && (c.Status == ChapterStatus.Done || c.Status == ChapterStatus.Review))
            .OrderBy(c => c.Number)
            .ToList();

        return PromptTemplates.EstablishedFacts(bible, cards, threads, priorChapters);
    }

    /// <summary>
    /// Builds the full structured context block for the complete CheckAsync (Story Bible + all characters + all threads).
    /// Returns both the formatted context string and the fetched StoryBible to avoid a second DB round-trip.
    /// </summary>
    private async Task<(string context, StoryBible? bible)> BuildStructuredContextAsync(int bookId)
    {
        var bible = await Repo.GetStoryBibleAsync(bookId);
        var cards = (await Repo.GetCharacterCardsAsync(bookId)).ToList();
        var threads = (await Repo.GetPlotThreadsAsync(bookId)).ToList();
        return (PromptTemplates.CheckerStructuredContext(bible, cards, threads), bible);
    }
}
