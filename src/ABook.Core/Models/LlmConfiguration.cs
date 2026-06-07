using System.Text.Json.Serialization;

namespace ABook.Core.Models;

public class LlmConfiguration
{
    public int Id { get; set; }
    public int? BookId { get; set; }
    public int? UserId { get; set; }
    public LlmProvider Provider { get; set; } = LlmProvider.Ollama;
    public string ModelName { get; set; } = "llama3";
    public string Endpoint { get; set; } = "http://localhost:11434";
    /// <summary>
    /// API key for the LLM provider. Stored in plaintext — consider implementing
    /// EF Core value converter encryption for production use (TODO).
    /// </summary>
    public string? ApiKey { get; set; }
    public string? EmbeddingModelName { get; set; }

    [JsonIgnore] public Book? Book { get; set; }
    [JsonIgnore] public AppUser? User { get; set; }
}
