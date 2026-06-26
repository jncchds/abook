using ABook.Core.Interfaces;
using ABook.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace ABook.Infrastructure.VectorStore;

/// <summary>
/// Each public method creates its own short-lived <see cref="AppDbContext"/> via the factory so
/// concurrent callers (e.g. parallel RAG queries from multiple agents) never share a context
/// instance and trigger EF Core's "second operation" or identity-map conflicts.
/// </summary>
public class PgvectorVectorStoreService : IVectorStoreService
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public PgvectorVectorStoreService(IDbContextFactory<AppDbContext> factory)
    {
        _factory = factory;
    }

    // Table is created via EF Core migration — nothing to do at runtime.
    public Task EnsureCollectionAsync(int bookId, CancellationToken ct = default)
        => Task.CompletedTask;

    public async Task UpsertChunkAsync(int bookId, int chapterId, int chapterNumber, int chunkIndex,
        string text, ReadOnlyMemory<float> embedding, CancellationToken ct = default, int? chapterVersionId = null)
    {
        await using var context = await _factory.CreateDbContextAsync(ct);
        var vector = new Vector(embedding.ToArray());

        ChapterEmbedding? existing;
        if (chapterVersionId.HasValue)
        {
            existing = await context.ChapterEmbeddings
                .FirstOrDefaultAsync(
                    e => e.BookId == bookId && e.ChapterVersionId == chapterVersionId && e.ChunkIndex == chunkIndex,
                    ct);
        }
        else
        {
            existing = await context.ChapterEmbeddings
                .FirstOrDefaultAsync(
                    e => e.BookId == bookId && e.ChapterId == chapterId && e.ChunkIndex == chunkIndex
                         && e.ChapterVersionId == null,
                    ct);
        }

        if (existing is null)
        {
            context.ChapterEmbeddings.Add(new ChapterEmbedding
            {
                BookId = bookId,
                ChapterId = chapterId,
                ChapterNumber = chapterNumber,
                ChunkIndex = chunkIndex,
                ChapterVersionId = chapterVersionId,
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

        await context.SaveChangesAsync(ct);
    }

    public async Task<IEnumerable<ChunkResult>> SearchAsync(
        int bookId,
        ReadOnlyMemory<float> queryEmbedding,
        int topK = 5,
        IReadOnlyCollection<int>? scopeBookIds = null,
        CancellationToken ct = default)
    {
        await using var context = await _factory.CreateDbContextAsync(ct);
        var queryVector = new Vector(queryEmbedding.ToArray());
        var effectiveBookIds = (scopeBookIds is { Count: > 0 }
            ? scopeBookIds.Append(bookId)
            : [bookId])
            .Where(id => id > 0)
            .Distinct()
            .ToArray();

        if (effectiveBookIds.Length == 0)
            return [];

        // Only return chunks for:
        //   - versioned chunks: where the version is active AND the chapter is not archived
        //   - legacy (unversioned) chunks: where the chapter is not archived
        var rows = await context.Database.SqlQueryRaw<ChunkRow>(
            """
            SELECT ce."ChapterId", ce."ChapterNumber", ce."ChunkIndex", ce."Text",
                   CAST(1 - (ce."Embedding" <=> {3}::vector) AS real) AS "Score"
            FROM "ChapterEmbeddings" ce
            WHERE ce."BookId" = ANY({1})
              AND (
                (ce."ChapterVersionId" IS NOT NULL AND EXISTS (
                    SELECT 1 FROM "ChapterVersions" cv
                    JOIN "Chapters" c ON c."Id" = cv."ChapterId"
                    WHERE cv."Id" = ce."ChapterVersionId"
                      AND cv."IsActive" = TRUE
                      AND c."IsArchived" = FALSE
                ))
                OR (ce."ChapterVersionId" IS NULL AND EXISTS (
                    SELECT 1 FROM "Chapters" c
                    WHERE c."Id" = ce."ChapterId"
                      AND c."IsArchived" = FALSE
                ))
              )
            ORDER BY ce."Embedding" <=> {0}::vector
            LIMIT {2}
            """,
            queryVector, effectiveBookIds, topK, queryVector)
            .ToListAsync(ct);

        return rows.Select(r => new ChunkResult(r.ChapterId, r.ChapterNumber, r.ChunkIndex, r.Text, r.Score));
    }

    public async Task<int> CountChunksAsync(int bookId, CancellationToken ct = default)
    {
        await using var context = await _factory.CreateDbContextAsync(ct);
        return await context.ChapterEmbeddings
            .Where(e => e.BookId == bookId)
            .CountAsync(ct);
    }

    public async Task DeleteChapterChunksAsync(int bookId, int chapterId, CancellationToken ct = default)
    {
        await using var context = await _factory.CreateDbContextAsync(ct);
        await context.ChapterEmbeddings
            .Where(e => e.BookId == bookId && e.ChapterId == chapterId)
            .ExecuteDeleteAsync(ct);
    }

    public async Task DeleteVersionChunksAsync(int bookId, int chapterVersionId, CancellationToken ct = default)
    {
        await using var context = await _factory.CreateDbContextAsync(ct);
        await context.ChapterEmbeddings
            .Where(e => e.BookId == bookId && e.ChapterVersionId == chapterVersionId)
            .ExecuteDeleteAsync(ct);
    }

    public async Task DeleteCollectionAsync(int bookId, CancellationToken ct = default)
    {
        await using var context = await _factory.CreateDbContextAsync(ct);
        await context.ChapterEmbeddings
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
