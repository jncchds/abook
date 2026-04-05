#pragma warning disable SKEXP0001, SKEXP0010, SKEXP0070

using ABook.Core.Interfaces;
using ABook.Core.Models;
using ABook.Infrastructure.VectorStore;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ABook.Agents;

public class WriterAgent : AgentBase
{
    public WriterAgent(
        IBookRepository repo,
        ILlmProviderFactory llmFactory,
        IVectorStoreService vectorStore,
        IBookNotifier notifier,
        AgentRunStateService stateService)
        : base(repo, llmFactory, vectorStore, notifier, stateService) { }

    public async Task WriteAsync(int bookId, int chapterId, CancellationToken ct = default)
    {
        var book = await Repo.GetByIdAsync(bookId)
            ?? throw new InvalidOperationException($"Book {bookId} not found.");
        var chapter = await Repo.GetChapterAsync(bookId, chapterId)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");

        chapter.Status = ChapterStatus.Writing;
        await Repo.UpdateChapterAsync(chapter);
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.Writer, "Running", ct);

        var kernel = await GetKernelAsync(bookId);
        var config = await Repo.GetLlmConfigAsync(bookId, book.UserId)!;

        // Retrieve RAG context: semantically relevant passages from prior chapters
        var ragContext = chapter.Number > 1
            ? await GetRagContextAsync(bookId, chapter.Outline, 5, LlmFactory, config!)
            : string.Empty;

        // Retrieve the direct ending of the previous chapter for narrative continuity
        var prevEnding = await GetPreviousChapterEndingAsync(bookId, chapter.Number, paragraphCount: 3);

        var history = new ChatHistory();
        var systemPrompt = !string.IsNullOrWhiteSpace(book.WriterSystemPrompt)
            ? InterpolateSystemPrompt(book.WriterSystemPrompt, book)
            : $"""
            You are a creative fiction Writer. Write compelling, immersive prose in markdown.
            Book title: {book.Title}
            Genre: {book.Genre}
            Premise: {book.Premise}
            Write all content in {book.Language}.
            IMPORTANT: Do NOT begin your response with any chapter heading, title, or label.
            Start immediately with the narrative prose (a scene, action, dialogue, or description).
            {(ragContext.Length > 0 ? $"\nRelevant context from previous chapters (for consistency):\n{ragContext}" : "")}
            {(prevEnding.Length > 0 ? $"\nThe previous chapter ended with:\n{prevEnding}\nContinue the story naturally from this point." : "")}
            """;
        history.AddSystemMessage(systemPrompt);

        history.AddUserMessage($"""
            Write the full content for:
            Chapter {chapter.Number}: {chapter.Title}
            Outline: {chapter.Outline}

            Write at least 1000 words of narrative prose. Output markdown only. Do NOT include a chapter heading.
            """);

        var content = await StreamResponseAsync(kernel, history, bookId, chapterId, ct);

        // Strip any leading chapter heading the LLM may have added (e.g. "# Chapter 1: Title")
        // since the UI displays title separately
        content = StripLeadingChapterHeading(content, chapter.Number, chapter.Title);

        chapter.Content = content;
        chapter.Status = ChapterStatus.Review;
        await Repo.UpdateChapterAsync(chapter);

        await Notifier.NotifyChapterUpdatedAsync(bookId, chapterId, ct);
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.Writer, "Done", ct);

        // Index chapter in Qdrant for RAG (non-fatal — chapter is already saved)
        try { await IndexChapterAsync(bookId, chapter, kernel, config!, ct); }
        catch (OperationCanceledException) { throw; }
        catch { /* Qdrant unavailable — RAG context will be skipped for future chapters */ }
    }

    private async Task IndexChapterAsync(int bookId, Chapter chapter, Kernel kernel, LlmConfiguration config, CancellationToken ct)
    {
        await VectorStore.EnsureCollectionAsync(bookId, ct);
        await VectorStore.DeleteChapterChunksAsync(bookId, chapter.Id, ct);

        var chunks = TextChunker.Chunk(chapter.Content);
        var embedder = LlmFactory.CreateEmbeddingGeneration(config);

        for (int i = 0; i < chunks.Count; i++)
        {
            var embeddings = await embedder.GenerateEmbeddingsAsync([chunks[i]], cancellationToken: ct);
            var embedding = embeddings[0];
            await VectorStore.UpsertChunkAsync(bookId, chapter.Id, chapter.Number, i, chunks[i], embedding, ct);
        }
    }
}
