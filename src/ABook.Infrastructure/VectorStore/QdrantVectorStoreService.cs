using ABook.Core.Interfaces;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace ABook.Infrastructure.VectorStore;

public class QdrantVectorStoreService : IVectorStoreService
{
    private readonly QdrantClient _client;
    private const ulong VectorSize = 768; // nomic-embed-text default; overridden at runtime

    public QdrantVectorStoreService(QdrantClient client)
    {
        _client = client;
    }

    private static string CollectionName(int bookId) => $"book_{bookId}_chapters";

    public async Task EnsureCollectionAsync(int bookId, CancellationToken ct = default)
    {
        var name = CollectionName(bookId);
        var collections = await _client.ListCollectionsAsync(ct);
        if (!collections.Any(c => c == name))
        {
            await _client.CreateCollectionAsync(name,
                new VectorParams { Size = VectorSize, Distance = Distance.Cosine },
                cancellationToken: ct);
        }
    }

    public async Task UpsertChunkAsync(int bookId, int chapterId, int chapterNumber, int chunkIndex,
        string text, ReadOnlyMemory<float> embedding, CancellationToken ct = default)
    {
        var name = CollectionName(bookId);
        var id = BuildId(chapterId, chunkIndex);

        var point = new PointStruct
        {
            Id = new PointId { Num = id },
            Vectors = embedding.ToArray(),
            Payload =
            {
                ["book_id"] = bookId,
                ["chapter_id"] = chapterId,
                ["chapter_number"] = chapterNumber,
                ["chunk_index"] = chunkIndex,
                ["text"] = text
            }
        };

        await _client.UpsertAsync(name, [point], cancellationToken: ct);
    }

    public async Task<IEnumerable<Core.Interfaces.ChunkResult>> SearchAsync(
        int bookId, ReadOnlyMemory<float> queryEmbedding, int topK = 5, CancellationToken ct = default)
    {
        var name = CollectionName(bookId);

        // Gracefully handle the case where no chapters have been indexed yet (collection doesn't exist)
        var collections = await _client.ListCollectionsAsync(ct);
        if (!collections.Any(c => c == name))
            return [];

        var results = await _client.SearchAsync(name, queryEmbedding.ToArray(), limit: (ulong)topK,
            payloadSelector: true, cancellationToken: ct);

        return results.Select(r => new Core.Interfaces.ChunkResult(
            ChapterId: (int)(long)r.Payload["chapter_id"].IntegerValue,
            ChapterNumber: (int)(long)r.Payload["chapter_number"].IntegerValue,
            ChunkIndex: (int)(long)r.Payload["chunk_index"].IntegerValue,
            Text: r.Payload["text"].StringValue,
            Score: r.Score
        ));
    }

    public async Task DeleteChapterChunksAsync(int bookId, int chapterId, CancellationToken ct = default)
    {
        var name = CollectionName(bookId);
        await _client.DeleteAsync(name, new Filter
        {
            Must =
            {
                new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = "chapter_id",
                        Match = new Match { Integer = chapterId }
                    }
                }
            }
        }, cancellationToken: ct);
    }

    public async Task DeleteCollectionAsync(int bookId, CancellationToken ct = default)
    {
        var name = CollectionName(bookId);
        var collections = await _client.ListCollectionsAsync(ct);
        if (collections.Any(c => c == name))
            await _client.DeleteCollectionAsync(name, cancellationToken: ct);
    }

    // Produce a stable ulong point ID from chapterId + chunkIndex
    private static ulong BuildId(int chapterId, int chunkIndex) =>
        ((ulong)(uint)chapterId << 20) | (ulong)(uint)(chunkIndex & 0xFFFFF);
}
