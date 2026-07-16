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
    /// <summary>
    /// Sampling temperature (0–1). Null/empty means use ABook's default (0.8f).
    /// Blank in the UI → fall back to provider default.
    /// </summary>
    public float? Temperature { get; set; }
    /// <summary>Request timeout in milliseconds. Null = no override.</summary>
    public int? TimeoutMs { get; set; }
    /// <summary>
    /// Reasoning effort for reasoning-capable models (DeepSeek-R1, Qwen3).
    /// Values: "none" | "low" | "medium" | "high". Null = model default.
    /// </summary>
    public string? ReasoningEffort { get; set; }
    /// <summary>Maximum output tokens. Null = provider default.</summary>
    public int? MaxTokens { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public AppUser? User { get; set; }
}
