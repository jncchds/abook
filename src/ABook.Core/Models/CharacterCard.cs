using System.Text.Json.Serialization;

namespace ABook.Core.Models;

public class CharacterCard
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public string Name { get; set; } = string.Empty;
    public CharacterRole Role { get; set; } = CharacterRole.Supporting;
    public string PhysicalDescription { get; set; } = string.Empty;
    public string Personality { get; set; } = string.Empty;
    public string Backstory { get; set; } = string.Empty;
    public string GoalMotivation { get; set; } = string.Empty;
    public string Arc { get; set; } = string.Empty;
    public int? FirstAppearanceChapterNumber { get; set; }
    public string Notes { get; set; } = string.Empty;
    /// <summary>Canonical appearance wording for image prompts, written by the illustration planner; editable.</summary>
    public string VisualPrompt { get; set; } = string.Empty;
    /// <summary>Hash of the identity fields <see cref="VisualPrompt"/> was written from; a mismatch means it is stale.</summary>
    public string? VisualPromptSourceHash { get; set; }
    /// <summary>Seed shared by the character's portrait and every scene centred on them.</summary>
    public long? ImageSeed { get; set; }
    public bool IsArchived { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    [JsonIgnore] public Book Book { get; set; } = null!;
}
