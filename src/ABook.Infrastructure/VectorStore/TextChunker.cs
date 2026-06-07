namespace ABook.Infrastructure.VectorStore;

/// <summary>Text chunking utilities for pgvector embedding storage.</summary>
public static class TextChunker
{
    /// <summary>Default chunk size in words (~500 tokens).</summary>
    public const int DefaultChunkSize = 400;

    /// <summary>Default overlap between consecutive chunks in words.</summary>
    public const int DefaultOverlap = 50;

    /// <summary>Splits text into overlapping chunks of approximately <paramref name="chunkSize"/> words.</summary>
    public static IReadOnlyList<string> Chunk(string text, int chunkSize = DefaultChunkSize, int overlap = DefaultOverlap)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var chunks = new List<string>();
        int step = Math.Max(1, chunkSize - overlap);

        for (int i = 0; i < words.Length; i += step)
        {
            var slice = words.Skip(i).Take(chunkSize);
            chunks.Add(string.Join(' ', slice));
            if (i + chunkSize >= words.Length) break;
        }

        return chunks;
    }
}
