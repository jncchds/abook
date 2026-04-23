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
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Book Book { get; set; } = null!;
}
