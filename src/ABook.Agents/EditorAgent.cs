#pragma warning disable SKEXP0001, SKEXP0010, SKEXP0070

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

        var history = new ChatHistory();
        var bible = await Repo.GetStoryBibleAsync(bookId);
        var systemPrompt = !string.IsNullOrWhiteSpace(book.EditorSystemPrompt)
            ? InterpolateSystemPrompt(book.EditorSystemPrompt, book, bible)
            : InterpolateSystemPrompt(DefaultPrompts.Editor, book, bible);
        history.AddSystemMessage(systemPrompt);

        // Build fix request from structured checker result and optional human notes
        var sb = new System.Text.StringBuilder();
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
