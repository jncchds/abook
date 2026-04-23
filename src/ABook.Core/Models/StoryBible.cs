namespace ABook.Core.Models;

public class StoryBible
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public string SettingDescription { get; set; } = string.Empty;
    public string TimePeriod { get; set; } = string.Empty;
    public string Themes { get; set; } = string.Empty;
    public string ToneAndStyle { get; set; } = string.Empty;
    public string WorldRules { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Book Book { get; set; } = null!;
}
