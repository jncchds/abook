using System.Text.Json.Serialization;

namespace ABook.Core.Models;

/// <summary>
/// A part of the book's plan spanning one or more chapters. Planning works in sections; a new section has one chapter.
/// It has a chapter's plan fields but no prose — its chapters hold that. A section with a single active chapter mirrors
/// that chapter's plan (saving either keeps them equal); once it has several chapters, its plan is the overall plan
/// and each chapter has its own detailed one.
/// </summary>
public class Section
{
    public int Id { get; set; }
    public int BookId { get; set; }
    /// <summary>1..N over the active sections in order; an archived section keeps the number it last had.</summary>
    public int Number { get; set; }
    /// <summary>Place among the book's sections, archived ones included. 1..N, dense.</summary>
    public int SortOrder { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Outline { get; set; } = string.Empty;
    public string PovCharacter { get; set; } = string.Empty;
    public string CharactersInvolvedJson { get; set; } = "[]";
    public string PlotThreadsJson { get; set; } = "[]";
    public string ForeshadowingNotes { get; set; } = string.Empty;
    public string PayoffNotes { get; set; } = string.Empty;
    /// <summary>True when none of its chapters is active — kept by the repository, never set on its own.</summary>
    public bool IsArchived { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    [JsonIgnore] public Book Book { get; set; } = null!;
    [JsonIgnore] public ICollection<Chapter> Chapters { get; set; } = new List<Chapter>();

    /// <summary>The plan fields a section shares with a chapter (and mirrors from its only chapter).</summary>
    public static readonly string[] PlanFields =
    [
        nameof(Title), nameof(Outline), nameof(PovCharacter), nameof(CharactersInvolvedJson), nameof(PlotThreadsJson),
        nameof(ForeshadowingNotes), nameof(PayoffNotes),
    ];

    /// <summary>Copies the plan fields from a chapter; true when anything changed.</summary>
    public bool CopyPlanFrom(Chapter c)
    {
        var changed = Title != c.Title || Outline != c.Outline || PovCharacter != c.PovCharacter
            || CharactersInvolvedJson != c.CharactersInvolvedJson || PlotThreadsJson != c.PlotThreadsJson
            || ForeshadowingNotes != c.ForeshadowingNotes || PayoffNotes != c.PayoffNotes;
        if (!changed) return false;
        Title = c.Title;
        Outline = c.Outline;
        PovCharacter = c.PovCharacter;
        CharactersInvolvedJson = c.CharactersInvolvedJson;
        PlotThreadsJson = c.PlotThreadsJson;
        ForeshadowingNotes = c.ForeshadowingNotes;
        PayoffNotes = c.PayoffNotes;
        return true;
    }
}
