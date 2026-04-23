namespace ABook.Core.Models;

public class Chapter
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public int Number { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Outline { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public ChapterStatus Status { get; set; } = ChapterStatus.Outlined;
    public string PovCharacter { get; set; } = string.Empty;
    public string CharactersInvolvedJson { get; set; } = "[]";
    public string PlotThreadsJson { get; set; } = "[]";
    public string ForeshadowingNotes { get; set; } = string.Empty;
    public string PayoffNotes { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Book Book { get; set; } = null!;
    public ICollection<AgentMessage> AgentMessages { get; set; } = new List<AgentMessage>();
}
