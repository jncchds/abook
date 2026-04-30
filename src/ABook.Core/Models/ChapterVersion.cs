namespace ABook.Core.Models;

public class ChapterVersion
{
    public int Id { get; set; }
    public int ChapterId { get; set; }
    public int BookId { get; set; }
    public int VersionNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Outline { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public ChapterStatus Status { get; set; } = ChapterStatus.Outlined;
    public string PovCharacter { get; set; } = string.Empty;
    public string CharactersInvolvedJson { get; set; } = "[]";
    public string PlotThreadsJson { get; set; } = "[]";
    public string ForeshadowingNotes { get; set; } = string.Empty;
    public string PayoffNotes { get; set; } = string.Empty;
    /// <summary>Which process created this version: "agent:Writer", "agent:Editor", "user", etc.</summary>
    public string CreatedBy { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    /// <summary>True when pgvector embeddings exist for this version (allows stale-RAG warning in UI).</summary>
    public bool HasEmbeddings { get; set; }
    public DateTime CreatedAt { get; set; }

    public Chapter Chapter { get; set; } = null!;
}
