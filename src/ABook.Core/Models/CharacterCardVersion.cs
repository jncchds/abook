using System.Text.Json.Serialization;

namespace ABook.Core.Models;

/// <summary>
/// Immutable snapshot of a <see cref="CharacterCard"/> at a specific point in time.
/// A new version is created on every user edit and on initial agent generation.
/// </summary>
public class CharacterCardVersion
{
    public int Id { get; set; }
    public int CharacterCardId { get; set; }
    public int BookId { get; set; }
    public int VersionNumber { get; set; }

    // Snapshot of all CharacterCard fields at this version
    public string Name { get; set; } = string.Empty;
    public CharacterRole Role { get; set; } = CharacterRole.Supporting;
    public string PhysicalDescription { get; set; } = string.Empty;
    public string Personality { get; set; } = string.Empty;
    public string Backstory { get; set; } = string.Empty;
    public string GoalMotivation { get; set; } = string.Empty;
    public string Arc { get; set; } = string.Empty;
    public int? FirstAppearanceChapterNumber { get; set; }
    public string Notes { get; set; } = string.Empty;

    /// <summary>"user", "agent:Characters", "restore"</summary>
    public string CreatedBy { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    [JsonIgnore] public CharacterCard CharacterCard { get; set; } = null!;
}
