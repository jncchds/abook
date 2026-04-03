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
        IBookNotifier notifier)
        : base(repo, llmFactory, vectorStore, notifier) { }

    public async Task CheckAsync(int bookId, CancellationToken ct = default)
    {
        var book = await Repo.GetByIdWithDetailsAsync(bookId)
            ?? throw new InvalidOperationException($"Book {bookId} not found.");

        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.ContinuityChecker, "Running", ct);

        var kernel = await GetKernelAsync(bookId);
        var doneChapters = book.Chapters.Where(c => c.Status == ChapterStatus.Done || c.Status == ChapterStatus.Review).ToList();

        if (doneChapters.Count < 2)
        {
            await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.ContinuityChecker, "Done", ct);
            return;
        }

        // Build a compact synopsis from chapter outlines + first 500 chars of each chapter
        var synopsis = string.Join("\n\n", doneChapters.Select(c =>
            $"Chapter {c.Number}: {c.Title}\nOutline: {c.Outline}\nContent excerpt: {c.Content[..Math.Min(500, c.Content.Length)]}..."));

        var history = new ChatHistory();
        history.AddSystemMessage($"""
            You are a Continuity Checker for fiction manuscripts. Your job is to identify plot holes,
            character inconsistencies, timeline errors, and factual contradictions across chapters.
            Output a JSON array of issues, each with:
              "description" (string), "chapterNumbers" (int[]), "suggestion" (string).
            If no issues found, output an empty array [].
            Book: {book.Title} | Genre: {book.Genre}
            """);

        history.AddUserMessage($"Review these chapters for continuity issues:\n\n{synopsis}");

        var response = await StreamResponseAsync(kernel, history, bookId, null, ct);

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
    }
}
