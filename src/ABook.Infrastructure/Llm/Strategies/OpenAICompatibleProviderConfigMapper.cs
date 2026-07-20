using ABook.Core.Models;
using Microsoft.Extensions.AI;
using OpenAI;

namespace ABook.Infrastructure.Llm.Strategies;

/// <summary>
/// Config mapper for OpenAI-compatible endpoints (LMStudio, OpenRouter, Groq, Together, etc.).
/// Same mapping as OpenAI but uses the custom HTTP streaming client that properly handles
/// reasoning content in SSE chunks. Embedding generation reuses the existing OpenAI pattern.
/// </summary>
public class OpenAICompatibleProviderConfigMapper : IProviderConfigMapper
{
    public LlmProvider Provider => LlmProvider.OpenAICompatible;

    public ChatOptions BuildChatOptions(LlmConfiguration config, string? jsonSchema = null)
    {
        var options = new ChatOptions
        {
            Temperature = (float)(config.Temperature > 0 ? config.Temperature : 0.7f),
            MaxOutputTokens = config.MaxTokens,
        };

        if (!string.IsNullOrWhiteSpace(jsonSchema))
        {
            var jsonEl = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(jsonSchema);
            options.ResponseFormat = ChatResponseFormat.ForJsonSchema(jsonEl, "structured_output", "");
        }

        // Map reasoning effort via ChatOptions.Reasoning (supported by some compatible endpoints)
        if (!string.IsNullOrWhiteSpace(config.ReasoningEffort) && config.ReasoningEffort != "none")
        {
            options.Reasoning = new ReasoningOptions
            {
                Effort = Enum.Parse<ReasoningEffort>(config.ReasoningEffort, true)
            };
        }

        return options;
    }

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGeneration(LlmConfiguration config)
    {
        var embeddingModel = config.EmbeddingModelName ?? "text-embedding-3-small";
        if (string.IsNullOrWhiteSpace(config.Endpoint))
            throw new InvalidOperationException("Endpoint is required for OpenAI-compatible providers.");

        return OpenAIProviderHelpers.CreateOpenAIClient(config.Endpoint, config.ApiKey)
            .GetEmbeddingClient(embeddingModel)
            .AsIEmbeddingGenerator();
    }
}
