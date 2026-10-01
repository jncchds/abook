using System.Text.Json.Serialization;

namespace ABook.Core.Models;

/// <summary>
/// A book's LLM settings. Every book owns exactly one, copied from its owner's default preset when the book
/// is created (or from the base book for a sequel); a book is never tied to a preset afterwards.
/// </summary>
public class LlmConfiguration : IEditableImageGenSettings
{
    public const string MissingMessage =
        "This book has no LLM settings. Open the book's Settings page and choose a preset (or fill in the fields), " +
        "and set a default preset on the Presets page so new books get one automatically.";

    public int Id { get; set; }
    public int BookId { get; set; }
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

    // ── Image generation (see IImageGenSettings) ──────────────────────────────
    public ImageProvider? ImageProvider { get; set; }
    public string? ImageEndpoint { get; set; }
    /// <summary>Stored in plaintext, like <see cref="ApiKey"/>.</summary>
    public string? ImageApiKey { get; set; }
    public string? ImageModel { get; set; }
    public int? ImageSteps { get; set; }
    public double? ImageCfg { get; set; }
    public int? ImagePortraitWidth { get; set; }
    public int? ImagePortraitHeight { get; set; }
    public int? ImageLandscapeWidth { get; set; }
    public int? ImageLandscapeHeight { get; set; }
    public int? ImageTimeoutMs { get; set; }
    public string? ImageVisualStyle { get; set; }
    public int? ImagesPerChapter { get; set; }
    public bool UnloadLlmBeforeRender { get; set; }

    [JsonIgnore] public Book? Book { get; set; }
}
