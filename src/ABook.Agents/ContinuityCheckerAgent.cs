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

    public async Task<string> CheckAsync(int bookId, CancellationToken ct = default)
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

        // Build a compact synopsis from chapter outlines + first 800 chars of each chapter
        var synopsis = string.Join("\n\n", doneChapters.Select(c =>
            $"Chapter {c.Number}: {c.Title}\nOutline: {c.Outline}\nContent excerpt: {c.Content[..Math.Min(800, c.Content?.Length ?? 0)]}..."));

        var history = new ChatHistory();
        var systemPrompt = !string.IsNullOrWhiteSpace(book.ContinuityCheckerSystemPrompt)
            ? book.ContinuityCheckerSystemPrompt
            : $"""
            You are a Continuity Checker for fiction manuscripts. Your job is to identify plot holes,
            character inconsistencies, timeline errors, and factual contradictions across chapters.
            Write a concise report in plain prose. For each issue found, state the problem, which
            chapters are affected, and a suggested fix. Group related issues together.
            If no issues are found, write a brief summary of what was checked and confirm the
            manuscript is consistent so far.
            Book: {book.Title} | Genre: {book.Genre} | Language: {book.Language}
            """;
        history.AddSystemMessage(systemPrompt);

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
        return response;
    }
}
