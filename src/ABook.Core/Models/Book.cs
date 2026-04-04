namespace ABook.Core.Models;

public class Book
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Premise { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public int TargetChapterCount { get; set; }
    public BookStatus Status { get; set; } = BookStatus.Draft;
    public string Language { get; set; } = "English";
    public string? PlannerSystemPrompt { get; set; }
    public string? WriterSystemPrompt { get; set; }
    public string? EditorSystemPrompt { get; set; }
    public string? ContinuityCheckerSystemPrompt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public int? UserId { get; set; }
    public AppUser? User { get; set; }

    public ICollection<Chapter> Chapters { get; set; } = new List<Chapter>();
    public ICollection<AgentMessage> AgentMessages { get; set; } = new List<AgentMessage>();
    public ICollection<LlmConfiguration> LlmConfigurations { get; set; } = new List<LlmConfiguration>();
}
