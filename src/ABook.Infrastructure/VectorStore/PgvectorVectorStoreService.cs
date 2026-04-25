using ABook.Core.Interfaces;
using ABook.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace ABook.Infrastructure.VectorStore;

public class PgvectorVectorStoreService : IVectorStoreService
{
    private readonly AppDbContext _context;

    public PgvectorVectorStoreService(AppDbContext context)
    {
        _context = context;
    }

    // Table is created via EF Core migration — nothing to do at runtime.
    public Task EnsureCollectionAsync(int bookId, CancellationToken ct = default)
        => Task.CompletedTask;

    public async Task UpsertChunkAsync(int bookId, int chapterId, int chapterNumber, int chunkIndex,
        string text, ReadOnlyMemory<float> embedding, CancellationToken ct = default)
    {
        var vector = new Vector(embedding.ToArray());

        var existing = await _context.ChapterEmbeddings
            .FirstOrDefaultAsync(
                e => e.BookId == bookId && e.ChapterId == chapterId && e.ChunkIndex == chunkIndex,
                ct);

        if (existing is null)
        {
            _context.ChapterEmbeddings.Add(new ChapterEmbedding
            {
                BookId = bookId,
                ChapterId = chapterId,
                ChapterNumber = chapterNumber,
                ChunkIndex = chunkIndex,
                Text = text,
                Embedding = vector
            });
        }
        else
        {
            existing.ChapterNumber = chapterNumber;
            existing.Text = text;
            existing.Embedding = vector;
        }

        await _context.SaveChangesAsync(ct);
    }

    public async Task<IEnumerable<ChunkResult>> SearchAsync(
        int bookId, ReadOnlyMemory<float> queryEmbedding, int topK = 5, CancellationToken ct = default)
    {
        var queryVector = new Vector(queryEmbedding.ToArray());

        // {0} = ORDER BY query vector, {1} = bookId, {2} = topK, {3} = SELECT score query vector
        var rows = await _context.Database.SqlQueryRaw<ChunkRow>(
            """
            SELECT "ChapterId", "ChapterNumber", "ChunkIndex", "Text",
                   CAST(1 - ("Embedding" <=> {3}::vector) AS real) AS "Score"
            FROM "ChapterEmbeddings"
            WHERE "BookId" = {1}
            ORDER BY "Embedding" <=> {0}::vector
            LIMIT {2}
            """,
            queryVector, bookId, topK, queryVector)
            .ToListAsync(ct);

        return rows.Select(r => new ChunkResult(r.ChapterId, r.ChapterNumber, r.ChunkIndex, r.Text, r.Score));
    }

    public async Task DeleteChapterChunksAsync(int bookId, int chapterId, CancellationToken ct = default)
    {
        await _context.ChapterEmbeddings
            .Where(e => e.BookId == bookId && e.ChapterId == chapterId)
            .ExecuteDeleteAsync(ct);
    }

    public async Task DeleteCollectionAsync(int bookId, CancellationToken ct = default)
    {
        await _context.ChapterEmbeddings
            .Where(e => e.BookId == bookId)
            .ExecuteDeleteAsync(ct);
    }

    private sealed class ChunkRow
    {
        public int ChapterId { get; set; }
        public int ChapterNumber { get; set; }
        public int ChunkIndex { get; set; }
        public string Text { get; set; } = string.Empty;
        public float Score { get; set; }
    }
}
