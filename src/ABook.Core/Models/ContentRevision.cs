namespace ABook.Core.Models;

/// <summary>What happened to a row recorded in the change log.</summary>
public enum ContentRevisionOp { Insert, Update, Delete }

/// <summary>
/// One entry of the book's change log: the full state of one content row (the book itself, a chapter, the Story
/// Bible, a character, a plot thread, a clarification, a book-chat proposal) right after it was written — or, for a
/// delete, the state it had. Written by <c>AppDbContext</c> on every save, whatever code made the change, so the
/// state of everything at any moment since the log began is "the newest revision of each row at or before then".
/// That is what Rewind restores. Rows that existed when the log began get a baseline revision (<see cref="IsBaseline"/>).
/// </summary>
public class ContentRevision
{
    public long Id { get; set; }
    public int BookId { get; set; }
    /// <summary>The entity's CLR type name: Book, Chapter, StoryBible, CharacterCard, PlotThread, Clarification, ChatProposal.</summary>
    public string EntityType { get; set; } = string.Empty;
    public int EntityId { get; set; }
    public ContentRevisionOp Op { get; set; }
    /// <summary>The row's scalar columns as a JSON object (property name → value).</summary>
    public string DataJson { get; set; } = "{}";
    /// <summary>Hash of <see cref="DataJson"/> without timestamps — a save that changed nothing records nothing.</summary>
    public string DataHash { get; set; } = string.Empty;
    /// <summary>The state the row already had when the change log began.</summary>
    public bool IsBaseline { get; set; }
    public DateTime At { get; set; }
}
