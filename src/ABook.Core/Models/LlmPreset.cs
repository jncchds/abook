namespace ABook.Core.Models;

public class LlmPreset
{
    public int Id { get; set; }
    public int? UserId { get; set; }
    public string Name { get; set; } = "";
    public LlmProvider Provider { get; set; } = LlmProvider.Ollama;
    public string ModelName { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string? ApiKey { get; set; }
    public string? EmbeddingModelName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public AppUser? User { get; set; }
}
