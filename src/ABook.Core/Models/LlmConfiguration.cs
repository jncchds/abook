namespace ABook.Core.Models;

public class LlmConfiguration
{
    public int Id { get; set; }
    public int? BookId { get; set; }
    public int? UserId { get; set; }
    public LlmProvider Provider { get; set; } = LlmProvider.Ollama;
    public string ModelName { get; set; } = "llama3";
    public string Endpoint { get; set; } = "http://localhost:11434";
    public string? ApiKey { get; set; }
    public string? EmbeddingModelName { get; set; }

    public Book? Book { get; set; }
    public AppUser? User { get; set; }
}
