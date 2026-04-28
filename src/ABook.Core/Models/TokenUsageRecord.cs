namespace ABook.Core.Models;

public class TokenUsageRecord
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public int? ChapterId { get; set; }
    public AgentRole AgentRole { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public string? StepLabel { get; set; }
    public string? Endpoint { get; set; }
    public string? ModelName { get; set; }
    public DateTime CreatedAt { get; set; }

    public Book Book { get; set; } = null!;
    public Chapter? Chapter { get; set; }
}
