namespace ABook.Core.Interfaces;

public interface IVectorStoreService
{
    Task EnsureCollectionAsync(int bookId, CancellationToken ct = default);
    Task UpsertChunkAsync(int bookId, int chapterId, int chapterNumber, int chunkIndex, string text, ReadOnlyMemory<float> embedding, CancellationToken ct = default);
    Task<IEnumerable<ChunkResult>> SearchAsync(int bookId, ReadOnlyMemory<float> queryEmbedding, int topK = 5, CancellationToken ct = default);
    Task<int> CountChunksAsync(int bookId, CancellationToken ct = default);
    Task DeleteChapterChunksAsync(int bookId, int chapterId, CancellationToken ct = default);
    Task DeleteCollectionAsync(int bookId, CancellationToken ct = default);
}

public record ChunkResult(int ChapterId, int ChapterNumber, int ChunkIndex, string Text, float Score);
