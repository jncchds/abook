namespace ABook.Core.Models;

/// <summary>Copies the full set of LLM + image settings between presets and book configurations.</summary>
public static class LlmSettingsCopy
{
    public const float DefaultTemperature = 0.8f;

    /// <summary>Overwrites every setting on a book configuration with the preset's values.</summary>
    public static void CopyFrom(this LlmConfiguration target, LlmPreset source)
    {
        target.Provider = source.Provider;
        target.ModelName = source.ModelName;
        target.Endpoint = source.Endpoint;
        target.ApiKey = source.ApiKey;
        target.EmbeddingModelName = source.EmbeddingModelName;
        target.Temperature = source.Temperature ?? DefaultTemperature;
        target.TimeoutMs = source.TimeoutMs;
        target.ReasoningEffort = source.ReasoningEffort;
        target.MaxTokens = source.MaxTokens;
        target.CopyImageSettingsFrom(source);
    }

    /// <summary>Overwrites every setting on a book configuration with another configuration's values.</summary>
    public static void CopyFrom(this LlmConfiguration target, LlmConfiguration source)
    {
        target.Provider = source.Provider;
        target.ModelName = source.ModelName;
        target.Endpoint = source.Endpoint;
        target.ApiKey = source.ApiKey;
        target.EmbeddingModelName = source.EmbeddingModelName;
        target.Temperature = source.Temperature;
        target.TimeoutMs = source.TimeoutMs;
        target.ReasoningEffort = source.ReasoningEffort;
        target.MaxTokens = source.MaxTokens;
        target.CopyImageSettingsFrom(source);
    }

    /// <summary>Overwrites every setting on a preset with another preset's values (the name and owner stay).</summary>
    public static void CopyFrom(this LlmPreset target, LlmPreset source)
    {
        target.Provider = source.Provider;
        target.ModelName = source.ModelName;
        target.Endpoint = source.Endpoint;
        target.ApiKey = source.ApiKey;
        target.EmbeddingModelName = source.EmbeddingModelName;
        target.Temperature = source.Temperature;
        target.TimeoutMs = source.TimeoutMs;
        target.ReasoningEffort = source.ReasoningEffort;
        target.MaxTokens = source.MaxTokens;
        target.CopyImageSettingsFrom(source);
    }
}
