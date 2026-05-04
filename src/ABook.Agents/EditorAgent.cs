#pragma warning disable SKEXP0001, SKEXP0010, SKEXP0070

using System.Text.Json;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ABook.Agents;

public class EditorAgent : AgentBase
{
    public EditorAgent(
        IBookRepository repo,
        ILlmProviderFactory llmFactory,
        IVectorStoreService vectorStore,
        IBookNotifier notifier,
        AgentRunStateService stateService,
        ILoggerFactory loggerFactory)
        : base(repo, llmFactory, vectorStore, notifier, stateService, loggerFactory) { }

    public async Task EditAsync(int bookId, int chapterId, CancellationToken ct = default,
        CheckerResult? checkerResult = null, string? humanAttentionPoints = null, bool finalizeStatus = true)
    {
        var book = await Repo.GetByIdAsync(bookId)
            ?? throw new InvalidOperationException($"Book {bookId} not found.");
        var chapter = await Repo.GetChapterAsync(bookId, chapterId)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");

        chapter.Status = ChapterStatus.Editing;
        await Repo.UpdateChapterAsync(chapter);
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.Editor, "Running", ct);

        var (kernel, config) = await GetKernelAsync(bookId);

        // Cross-chapter context: full synopsis spine + 4 targeted RAG queries
        var synopsesBlock = await BuildChapterSynopsesAsync(bookId, chapter.Number, ct);

        string charRag = string.Empty, locationRag = string.Empty, threadRag = string.Empty, phrasesRag = string.Empty;
        if (chapter.Number > 1)
        {
            List<string> charNames;
            try { charNames = JsonSerializer.Deserialize<List<string>>(chapter.CharactersInvolvedJson) ?? []; }
            catch { charNames = []; }
            var charQuery = charNames.Count > 0
                ? $"character appearance description personality {string.Join(' ', charNames)}"
                : "character appearance description personality backstory";

            var locationQuery = $"location place setting description {chapter.Outline}";

            List<string> threadNames;
            try { threadNames = JsonSerializer.Deserialize<List<string>>(chapter.PlotThreadsJson) ?? []; }
            catch { threadNames = []; }
            var threadQuery = threadNames.Count > 0
                ? $"plot thread events foreshadowing {string.Join(' ', threadNames)}"
                : $"plot events sequence foreshadowing {chapter.Outline}";

            var phrasesQuery = "repeated descriptions recurring phrases re-introduction established facts";

            var ragTasks = new[]
            {
                GetRagContextAsync(bookId, charQuery,    4, LlmFactory, config, chapter.Id, ct),
                GetRagContextAsync(bookId, locationQuery, 3, LlmFactory, config, chapter.Id, ct),
                GetRagContextAsync(bookId, threadQuery,  3, LlmFactory, config, chapter.Id, ct),
                GetRagContextAsync(bookId, phrasesQuery, 4, LlmFactory, config, chapter.Id, ct),
            };
            await Task.WhenAll(ragTasks);
            charRag     = ragTasks[0].Result;
            locationRag = ragTasks[1].Result;
            threadRag   = ragTasks[2].Result;
            phrasesRag  = ragTasks[3].Result;
        }

        var history = new ChatHistory();
        var bible = await Repo.GetStoryBibleAsync(bookId);
        var systemPrompt = !string.IsNullOrWhiteSpace(book.EditorSystemPrompt)
            ? InterpolateSystemPrompt(book.EditorSystemPrompt, book, bible)
            : InterpolateSystemPrompt(DefaultPrompts.Editor, book, bible);
        history.AddSystemMessage(systemPrompt);

        // Build fix request from structured checker result and optional human notes
        var sb = new System.Text.StringBuilder();

        // Cross-chapter context at the top so the Editor can catch re-introductions and echoed content
        if (synopsesBlock.Length > 0)
        {
            sb.AppendLine("## Story So Far \u2014 Chapter Synopses");
            sb.AppendLine(synopsesBlock);
            sb.AppendLine();
        }
        bool hasRagContext = !string.IsNullOrWhiteSpace(charRag) || !string.IsNullOrWhiteSpace(locationRag)
                          || !string.IsNullOrWhiteSpace(threadRag) || !string.IsNullOrWhiteSpace(phrasesRag);
        if (hasRagContext)
        {
            sb.AppendLine("## Prior Chapter Passages (use to identify re-introductions and repeated content)");
            if (!string.IsNullOrWhiteSpace(charRag))     { sb.AppendLine("\n### Character Details");                              sb.AppendLine(charRag); }
            if (!string.IsNullOrWhiteSpace(locationRag)) { sb.AppendLine("\n### Location & Setting Details");                     sb.AppendLine(locationRag); }
            if (!string.IsNullOrWhiteSpace(threadRag))   { sb.AppendLine("\n### Plot Thread Context");                            sb.AppendLine(threadRag); }
            if (!string.IsNullOrWhiteSpace(phrasesRag))  { sb.AppendLine("\n### Potentially Repeated Phrases & Descriptions");   sb.AppendLine(phrasesRag); }
            sb.AppendLine();
        }

        sb.AppendLine($"Please fix Chapter {chapter.Number}: {chapter.Title}");

        bool hasCheckerIssues = checkerResult is { HasIssues: true };
        if (hasCheckerIssues)
        {
            if (checkerResult!.ContinuityIssues.Length > 0)
            {
                sb.AppendLine("\n**Continuity issues to fix:**");
                foreach (var issue in checkerResult.ContinuityIssues)
                    sb.AppendLine($"- {issue}");
            }
            if (checkerResult.StyleIssues.Length > 0)
            {
                sb.AppendLine("\n**Style issues to fix:**");
                foreach (var issue in checkerResult.StyleIssues)
                    sb.AppendLine($"- {issue}");
            }
        }

        if (!string.IsNullOrWhiteSpace(humanAttentionPoints))
        {
            sb.AppendLine("\n**Additional attention points from the author:**");
            sb.AppendLine(humanAttentionPoints.Trim());
        }

        if (!hasCheckerIssues && string.IsNullOrWhiteSpace(humanAttentionPoints))
        {
            sb.AppendLine("\nNo specific issues listed — make only minor polish corrections if needed.");
        }

        sb.AppendLine($"\nOriginal content:\n{chapter.Content}");
        history.AddUserMessage(sb.ToString());

        var edited = await StreamResponseAsync(kernel, config, history, bookId, chapterId, AgentRole.Editor, ct);

        // Split off the editorial notes section.
        // We search for the LAST occurrence of any level-2 heading that looks like notes/feedback
        // (e.g. "## Editorial Notes", "## Editor's Notes", "## Feedback", "## Changes Made").
        // If not found, the whole response is saved as prose.
        var notesMatch = System.Text.RegularExpressions.Regex.Match(
            edited,
            @"^##\s+(editorial notes?|editor'?s? notes?|feedback|changes made|notes?|revisions?)\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Multiline);

        string prose;
        if (notesMatch.Success)
        {
            var notes = edited[notesMatch.Index..];
            prose = edited[..notesMatch.Index].Trim();
            await Repo.AddMessageAsync(new AgentMessage
            {
                BookId = bookId,
                ChapterId = chapterId,
                AgentRole = AgentRole.Editor,
                MessageType = MessageType.Feedback,
                Content = notes,
                IsResolved = true
            });
        }
        else
        {
            prose = edited;
        }

        // Strip any leading chapter heading the LLM may have added (e.g. "# Chapter 1: Title")
        chapter.Content = StripLeadingChapterHeading(prose, chapter.Number, chapter.Title);

        chapter.Status = finalizeStatus ? ChapterStatus.Done : ChapterStatus.Review;
        await Repo.UpdateChapterAsync(chapter);
        await Notifier.NotifyChapterUpdatedAsync(bookId, chapterId, ct);
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.Editor, "Done", ct);
    }
}
