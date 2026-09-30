namespace ABook.Core.Models;

/// <summary>
/// Planning data of one ancestor of a continuation book, used to give the planning agents the
/// established facts of the earlier books. Collections exclude archived items.
/// </summary>
public sealed record AncestorBookContext(
    Book Book,
    StoryBible? Bible,
    IReadOnlyList<CharacterCard> Characters,
    IReadOnlyList<PlotThread> PlotThreads,
    IReadOnlyList<Chapter> Chapters,
    bool IsDirectParent);
