using Pgvector;

namespace ABook.Infrastructure.VectorStore;

public class ChapterEmbedding
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public int ChapterId { get; set; }
    public int ChapterNumber { get; set; }
    public int ChunkIndex { get; set; }
    public string Text { get; set; } = string.Empty;
    public Vector Embedding { get; set; } = null!;
}
