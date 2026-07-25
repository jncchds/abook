using ABook.Infrastructure.VectorStore;

namespace ABook.Tests;

public class TextChunkerTests
{
    [Fact]
    public void Chunk_EmptyString_ReturnsEmpty()
    {
        var result = TextChunker.Chunk("");
        Assert.Empty(result);
    }

    [Fact]
    public void Chunk_WhitespaceOnly_ReturnsEmpty()
    {
        var result = TextChunker.Chunk("   \t  ");
        Assert.Empty(result);
    }

    [Fact]
    public void Chunk_SingleWord_ReturnsOneChunk()
    {
        var result = TextChunker.Chunk("hello");
        Assert.Single(result);
        Assert.Equal("hello", result[0]);
    }

    [Fact]
    public void Chunk_TextShorterThanChunkSize_ReturnsOneChunk()
    {
        var words = string.Join(' ', Enumerable.Repeat("word", 10));
        var result = TextChunker.Chunk(words, chunkSize: 400, overlap: 50);
        Assert.Single(result);
    }

    [Fact]
    public void Chunk_TextExactlyChunkSize_ReturnsOneChunk()
    {
        var words = string.Join(' ', Enumerable.Repeat("w", 400));
        var result = TextChunker.Chunk(words, chunkSize: 400, overlap: 50);
        Assert.Single(result);
    }

    [Fact]
    public void Chunk_LargeText_ProducesMultipleChunks()
    {
        var words = string.Join(' ', Enumerable.Repeat("word", 800));
        var result = TextChunker.Chunk(words, chunkSize: 400, overlap: 50);
        Assert.True(result.Count > 1);
    }

    [Fact]
    public void Chunk_OverlapIsRespected()
    {
        // 500 words, chunk=400, overlap=50 → step=350 → chunks at 0 and 350
        var allWords = Enumerable.Range(1, 500).Select(i => $"w{i}").ToArray();
        var text = string.Join(' ', allWords);
        var result = TextChunker.Chunk(text, chunkSize: 400, overlap: 50);

        Assert.Equal(2, result.Count);
        // second chunk starts at word 350 (0-indexed), which is w351
        Assert.StartsWith("w351", result[1]);
    }

    [Fact]
    public void Chunk_NoDuplicateFinalChunk()
    {
        // Exactly 2 * step words should produce exactly 2 chunks, not 3
        // step = chunkSize - overlap = 400 - 50 = 350
        var words = string.Join(' ', Enumerable.Repeat("word", 700));
        var result = TextChunker.Chunk(words, chunkSize: 400, overlap: 50);
        Assert.Equal(2, result.Count);
    }
}
