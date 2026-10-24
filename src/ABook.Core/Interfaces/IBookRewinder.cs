namespace ABook.Core.Interfaces;

/// <summary>What a rewind does to one row of the book.</summary>
public enum RewindChangeKind
{
    /// <summary>Back to how it was (saved as a new version / snapshot; the versions in between stay in its history).</summary>
    Restore,
    /// <summary>Created after the moment: archived (still in the archive, restorable as usual).</summary>
    Archive,
    /// <summary>Deleted after the moment: brought back.</summary>
    Recreate,
}

/// <summary>One change a rewind makes, in words for the preview.</summary>
/// <param name="EntityType">Book, Chapter, StoryBible, CharacterCard, PlotThread, Clarification or ChatProposal.</param>
/// <param name="Label">"Chapter 3: The Start", "Mortimer", "Story Bible"…</param>
/// <param name="Fields">For a restore: what goes back ("text", "outline", "status"…).</param>
public sealed record RewindChange(string EntityType, int EntityId, RewindChangeKind Kind, string Label, IReadOnlyList<string> Fields);

/// <summary>Everything a rewind to <see cref="At"/> would do. Nothing changes until it is applied.</summary>
/// <param name="HiddenChatMessages">Book-chat messages after the moment: hidden and never sent to the LLM again.</param>
/// <param name="Fingerprint">Identifies the book's state the preview was made from; a rewind confirmed with an older one is refused.</param>
public sealed record RewindPlan(
    int BookId,
    DateTime At,
    IReadOnlyList<RewindChange> Changes,
    int HiddenChatMessages,
    string Fingerprint)
{
    public bool IsEmpty => Changes.Count == 0 && HiddenChatMessages == 0;
}

/// <summary>What a rewind did — for notifications and follow-up (re-indexing new chapter versions).</summary>
public sealed record RewindResult(
    RewindPlan Plan,
    IReadOnlyList<int> ChangedChapterIds,
    IReadOnlyList<(int ChapterId, int VersionId)> VersionsToIndex,
    IReadOnlyList<int> HiddenChatMessageIds);

/// <summary>A moment the book can be rewound to, offered in the UI and in Telegram.</summary>
/// <param name="Kind">chat-before, chat-after, chapter-version, run-before, run-after.</param>
public sealed record RewindPoint(DateTime At, string Kind, string Label);

/// <summary>
/// Rewinds a whole book to an earlier moment: every logged row (book fields, chapters, Story Bible, characters, plot
/// threads, clarifications, book-chat proposals) gets the state it had then, as a new state — the versions in between
/// stay in each part's history — and book-chat messages after it are hidden. Works from the change log, so the
/// earliest moment is when the log began for the book.
/// </summary>
public interface IBookRewinder
{
    /// <summary>When the book's change log began: rewinding before it is not possible.</summary>
    Task<DateTime?> EarliestAsync(int bookId, CancellationToken ct = default);

    /// <summary>Recent moments to rewind to, newest first (older than <paramref name="before"/> when given).</summary>
    Task<IReadOnlyList<RewindPoint>> PointsAsync(int bookId, DateTime? before = null, int limit = 30, CancellationToken ct = default);

    /// <summary>
    /// The moment a book-chat message stands for: just before an author message, just after a reply (with the
    /// suggestions saved with it). Null when the message is not the book's.
    /// </summary>
    Task<DateTime?> ChatMessageMomentAsync(int bookId, int messageId, CancellationToken ct = default);

    Task<RewindPlan> PreviewAsync(int bookId, DateTime at, CancellationToken ct = default);

    /// <summary>
    /// Applies the rewind in one transaction. Throws <see cref="RewindStaleException"/> when
    /// <paramref name="fingerprint"/> is given and the book changed since that preview.
    /// </summary>
    Task<RewindResult> RewindAsync(int bookId, DateTime at, string? fingerprint, string? origin, CancellationToken ct = default);
}

public sealed class RewindStaleException() : Exception("The book changed since the preview. Preview the rewind again.");

public sealed class RewindTooEarlyException(DateTime earliest)
    : Exception($"The book's history starts at {earliest:u}; it can't be rewound to before that.")
{
    public DateTime Earliest { get; } = earliest;
}
