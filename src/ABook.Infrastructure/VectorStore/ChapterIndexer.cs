using ABook.Core.Interfaces;
using ABook.Core.Models;

namespace ABook.Infrastructure.VectorStore;

/// <summary>
/// Shared chapter-embedding routine. Agents index the versions they produce; the API indexes the
/// versions the author produces by editing a chapter by hand. Both go through here so a
/// human-written version is chunked, embedded and marked exactly like an agent-written one —
/// without that, RAG silently loses the chapter, because <c>SearchAsync</c> only returns chunks
/// belonging to the chapter's active version.
/// </summary>
public static class ChapterIndexer
{
    /// <summary>
    /// Chunks one chapter version, embeds every chunk, replaces that version's rows in the vector
    /// store, and flags the version as embedded.
    /// </summary>
    /// <param name="reportUsage">
    /// Called with the number of characters embedded and — when the run stopped early — the
    /// exception that stopped it, so the caller can bill the chunks that made it through.
    /// </param>
    /// <returns>False when the version is missing or has no content; true once indexed.</returns>
    public static async Task<bool> IndexVersionAsync(
        IBookRepository repo,
        ILlmProviderFactory llmFactory,
        IVectorStoreService vectorStore,
        LlmConfiguration config,
        int bookId, int chapterId, int chapterVersionId,
        Func<int, Exception?, Task>? reportUsage,
        CancellationToken ct)
    {
        await vectorStore.EnsureCollectionAsync(bookId, ct);

        var version = await repo.GetChapterVersionAsync(chapterId, chapterVersionId);
        if (version is null || string.IsNullOrEmpty(version.Content)) return false;

        var chapter = await repo.GetChapterAsync(bookId, chapterId);
        if (chapter is null) return false;

        await vectorStore.DeleteVersionChunksAsync(bookId, chapterVersionId, ct);

        var chunks = TextChunker.Chunk(version.Content);
        var embedder = llmFactory.CreateEmbeddingGeneration(config);

        int embeddedChars = 0;
        try
        {
            for (int i = 0; i < chunks.Count; i++)
            {
                var embeddings = await embedder.GenerateAsync([chunks[i]], cancellationToken: ct);
                await vectorStore.UpsertChunkAsync(
                    bookId, chapterId, chapter.Number, i, chunks[i], embeddings[0].Vector, ct, chapterVersionId);
                embeddedChars += chunks[i].Length;
            }
        }
        catch (Exception ex)
        {
            // Still bill the chunks that were embedded before the failure.
            if (reportUsage is not null) await reportUsage(embeddedChars, ex);
            throw;
        }

        version.HasEmbeddings = true;
        await repo.UpdateChapterVersionAsync(version);

        if (reportUsage is not null) await reportUsage(embeddedChars, null);
        return true;
    }
}
