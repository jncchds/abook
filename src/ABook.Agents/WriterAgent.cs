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
        IBookNotifier notifier)
        : base(repo, llmFactory, vectorStore, notifier) { }

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
        var config = await Repo.GetLlmConfigAsync(bookId)!;

        // Retrieve RAG context: prior chapter summaries
        var ragContext = chapter.Number > 1
            ? await GetRagContextAsync(bookId, chapter.Outline, 5, LlmFactory, config!)
            : string.Empty;

        var history = new ChatHistory();
        var systemPrompt = !string.IsNullOrWhiteSpace(book.WriterSystemPrompt)
            ? book.WriterSystemPrompt
            : $"""
            You are a creative fiction Writer. Write compelling, immersive prose in markdown.
            Book title: {book.Title}
            Genre: {book.Genre}
            Premise: {book.Premise}
            Write all content in {book.Language}.
            {(ragContext.Length > 0 ? $"\nRelevant context from previous chapters:\n{ragContext}" : "")}
            """;
        history.AddSystemMessage(systemPrompt);

        history.AddUserMessage($"""
            Write the full content for:
            Chapter {chapter.Number}: {chapter.Title}
            Outline: {chapter.Outline}

            Write at least 1000 words of narrative prose. Output markdown only.
            """);

        var content = await StreamResponseAsync(kernel, history, bookId, chapterId, ct);

        chapter.Content = content;
        chapter.Status = ChapterStatus.Review;
        await Repo.UpdateChapterAsync(chapter);

        // Index chapter in Qdrant for future RAG
        await IndexChapterAsync(bookId, chapter, kernel, config!, ct);

        await Notifier.NotifyChapterUpdatedAsync(bookId, chapterId, ct);
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.Writer, "Done", ct);
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
