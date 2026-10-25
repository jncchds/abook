using System.Text.Json.Serialization;

namespace ABook.Core.Models;

public class Chapter
{
    public int Id { get; set; }
    public int BookId { get; set; }
    /// <summary>
    /// The chapter's display number: 1..N over the book's active chapters in <see cref="SortOrder"/> order, kept up to
    /// date by the repository whenever chapters are added, moved, archived or restored. An archived chapter keeps the
    /// number it last had ("was Ch. N"). Never use it to find a chapter that can move — use the id.
    /// </summary>
    public int Number { get; set; }
    /// <summary>
    /// Where the chapter sits in the book, archived chapters included (they keep their place). 1..N, dense; a section's
    /// chapters are always together, in the order of the sections.
    /// </summary>
    public int SortOrder { get; set; }
    /// <summary>The section the chapter belongs to (every chapter has one; null only while being created).</summary>
    public int? SectionId { get; set; }
    /// <summary>Archived because its whole section was; restoring the section brings these chapters back.</summary>
    public bool ArchivedWithSection { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Outline { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public ChapterStatus Status { get; set; } = ChapterStatus.Outlined;
    public string PovCharacter { get; set; } = string.Empty;
    public string CharactersInvolvedJson { get; set; } = "[]";
    public string PlotThreadsJson { get; set; } = "[]";
    public string ForeshadowingNotes { get; set; } = string.Empty;
    public string PayoffNotes { get; set; } = string.Empty;
    public bool IsArchived { get; set; } = false;
    /// <summary>
    /// What the chapter's prose actually tells (not the plan), with its established facts — written by the
    /// summarizer after the chapter is written or changed in an Interactive book. Empty until then.
    /// </summary>
    public string Summary { get; set; } = string.Empty;
    /// <summary>The version <see cref="Summary"/> was written from; a different active version means it is stale.</summary>
    public int? SummaryVersionId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    [JsonIgnore] public Book Book { get; set; } = null!;
    [JsonIgnore] public Section? Section { get; set; }

    /// <summary>Copies the plan fields (title, outline, POV, characters, threads, notes) from a section.</summary>
    public void CopyPlanFrom(Section s)
    {
        Title = s.Title;
        Outline = s.Outline;
        PovCharacter = s.PovCharacter;
        CharactersInvolvedJson = s.CharactersInvolvedJson;
        PlotThreadsJson = s.PlotThreadsJson;
        ForeshadowingNotes = s.ForeshadowingNotes;
        PayoffNotes = s.PayoffNotes;
    }
    [JsonIgnore] public ICollection<AgentMessage> AgentMessages { get; set; } = new List<AgentMessage>();
    [JsonIgnore] public ICollection<ChapterVersion> Versions { get; set; } = new List<ChapterVersion>();
}
