namespace ABook.Core.Interfaces;

/// <summary>Embeds a chapter version for RAG in the background (author edits, rewinds).</summary>
public interface IChapterIndexQueue
{
    void QueueIndex(int bookId, int chapterId, int chapterVersionId);
}
