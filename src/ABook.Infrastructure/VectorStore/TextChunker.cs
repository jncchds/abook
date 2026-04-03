namespace ABook.Infrastructure.VectorStore;

public static class TextChunker
{
    /// <summary>Splits text into overlapping chunks of approximately <paramref name="chunkSize"/> words.</summary>
    public static IReadOnlyList<string> Chunk(string text, int chunkSize = 400, int overlap = 50)
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
