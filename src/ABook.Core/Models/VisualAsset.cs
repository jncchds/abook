using System.Text.Json.Serialization;

namespace ABook.Core.Models;

/// <summary>
/// A recurring visual element of the book — a prop, an outfit, a place, a creature or a vehicle — with
/// one canonical description. The illustration planner registers each one the first time an image shows
/// it and copies the description word for word into every later prompt that shows it again, which is
/// what keeps objects, clothes and places looking the same from image to image (the image model takes no
/// reference images). Editing the description replaces the old wording in every prompt that quotes it.
/// </summary>
public class VisualAsset
{
    public int Id { get; set; }
    public int BookId { get; set; }
    /// <summary>Short unique name within the book, e.g. "Mara's travelling cloak" or "the lighthouse".</summary>
    public string Name { get; set; } = string.Empty;
    public VisualAssetKind Kind { get; set; } = VisualAssetKind.Object;
    /// <summary>Canonical English appearance wording, pasted word for word into every prompt that shows it.</summary>
    public string Description { get; set; } = string.Empty;
    /// <summary>The character whose usual outfit this is; their portrait and every scene default to it.</summary>
    public int? CharacterCardId { get; set; }
    /// <summary>The chapter whose plan first showed it — informational, for the author.</summary>
    public int? FirstChapterNumber { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    [JsonIgnore] public Book Book { get; set; } = null!;
}
