#pragma warning disable SKEXP0001, SKEXP0010, SKEXP0070

using ABook.Core.Interfaces;
using ABook.Core.Models;
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
        AgentRunStateService stateService)
        : base(repo, llmFactory, vectorStore, notifier, stateService) { }

    /// <summary>
    /// Checks continuity. When <paramref name="chapterId"/> is provided, only reports issues
    /// introduced by that chapter against the preceding ones (ignoring pre-existing issues
    /// between earlier chapters). When null, performs a full manuscript review.
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

        // Determine whether we're doing a focused check or a full review
        var currentChapter = chapterId.HasValue
            ? doneChapters.FirstOrDefault(c => c.Id == chapterId.Value)
            : null;
        var previousChapters = currentChapter != null
            ? doneChapters.Where(c => c.Number < currentChapter.Number).ToList()
            : doneChapters;
        var chaptersToSummarise = currentChapter != null
            ? doneChapters.Where(c => c.Number <= currentChapter.Number).ToList()
            : doneChapters;

        // Build a compact synopsis from chapter outlines + first 800 chars of each chapter
        var synopsis = string.Join("\n\n", chaptersToSummarise.Select(c =>
            $"Chapter {c.Number}: {c.Title}\nOutline: {c.Outline}\nContent excerpt: {c.Content?[..Math.Min(800, c.Content?.Length ?? 0)]}..."));

        // Retrieve detailed passages via RAG for continuity-sensitive topics
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
                var part = await GetRagContextAsync(bookId, q, 4, LlmFactory, config);
                if (!string.IsNullOrWhiteSpace(part)) ragParts.Add(part);
            }
            if (ragParts.Count > 0)
                ragContext = string.Join("\n\n===\n\n", ragParts);
        }

        var history = new ChatHistory();

        string systemPrompt;
        if (!string.IsNullOrWhiteSpace(book.ContinuityCheckerSystemPrompt))
        {
            systemPrompt = InterpolateSystemPrompt(book.ContinuityCheckerSystemPrompt, book);
        }
        else if (currentChapter != null)
        {
            // Focused mode: only flag issues introduced by the current chapter
            systemPrompt = $"""
                You are a Continuity Checker for fiction manuscripts. Your job is to verify that
                the chapter under review does not contradict or conflict with what was established
                in the preceding chapters.
                IMPORTANT: Do NOT report issues that exist solely between the previous chapters.
                Focus only on contradictions, inconsistencies, or continuity breaks that are
                introduced by Chapter {currentChapter.Number} ("{currentChapter.Title}").
                Examine character details (names, appearance, backstory), timeline and sequence
                of events, and locations/settings.
                Write a concise report in plain prose. For each issue found, state clearly which
                detail in Chapter {currentChapter.Number} conflicts with what was established earlier
                and suggest a fix. If no new issues are found, confirm that Chapter
                {currentChapter.Number} is consistent with the material that precedes it.
                Book: {book.Title} | Genre: {book.Genre} | Language: {book.Language}
                """;
        }
        else
        {
            // Full review mode
            systemPrompt = $"""
                You are a Continuity Checker for fiction manuscripts. Your job is to identify plot holes,
                character inconsistencies, timeline errors, and factual contradictions across chapters.
                Write a concise report in plain prose. For each issue found, state the problem, which
                chapters are affected, and a suggested fix. Group related issues together.
                If no issues are found, write a brief summary of what was checked and confirm the
                manuscript is consistent so far.
                Book: {book.Title} | Genre: {book.Genre} | Language: {book.Language}
                """;
        }
        history.AddSystemMessage(systemPrompt);

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

            userMessage = string.IsNullOrWhiteSpace(ragContext)
                ? $"""
                    Check Chapter {currentChapter.Number} ("{currentChapter.Title}") for continuity issues
                    against the preceding chapters. Do NOT report problems between the preceding chapters.

                    ## Preceding Chapters (established facts)
                    {prevSynopsis}

                    ## Chapter Under Review
                    {currentExcerpt}
                    """
                : $"""
                    Check Chapter {currentChapter.Number} ("{currentChapter.Title}") for continuity issues
                    against the preceding chapters. Do NOT report problems between the preceding chapters.

                    ## Preceding Chapters (established facts)
                    {prevSynopsis}

                    ## Chapter Under Review
                    {currentExcerpt}

                    ## Detailed Passages (retrieved for continuity review)
                    {ragContext}
                    """;
        }
        else
        {
            userMessage = string.IsNullOrWhiteSpace(ragContext)
                ? $"Review these chapters for continuity issues:\n\n{synopsis}"
                : $"""
                    Review these chapters for continuity issues.

                    ## Chapter Summaries
                    {synopsis}

                    ## Detailed Passages (retrieved for continuity review)
                    {ragContext}
                    """;
        }
        history.AddUserMessage(userMessage);

        var response = await StreamResponseAsync(kernel, history, bookId, null, AgentRole.ContinuityChecker, ct);

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
}
