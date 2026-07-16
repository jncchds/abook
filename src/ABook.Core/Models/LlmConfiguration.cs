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
    /// <summary>
    /// Sampling temperature (0–1). Null/empty means use ABook's default (0.8f).
    /// </summary>
    public float Temperature { get; set; } = 0.8f;
    /// <summary>Request timeout in milliseconds. 0 or negative = no override.</summary>
    public int? TimeoutMs { get; set; }
    /// <summary>
    /// Reasoning effort for reasoning-capable models.
    /// Values: "none" | "low" | "medium" | "high". Null/empty = model default.
    /// </summary>
    public string? ReasoningEffort { get; set; }
    /// <summary>Maximum output tokens. 0 or negative = provider default.</summary>
    public int? MaxTokens { get; set; }

    [JsonIgnore] public Book? Book { get; set; }
    [JsonIgnore] public AppUser? User { get; set; }
}
