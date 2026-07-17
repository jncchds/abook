using ABook.Core.Models;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;

namespace ABook.Infrastructure.Llm.Strategies;

/// <summary>
/// Google AI Studio config mapper. Embeddings use Google's OpenAI-compatible endpoint via the existing
/// OpenAI SDK (text-embedding-004 is recommended). Chat uses our custom Gemini REST API client.
/// </summary>
public class GoogleAiStudioProviderConfigMapper : IProviderConfigMapper
{
    private const string OpenAICompatEndpoint = "https://generativelanguage.googleapis.com/v1beta/openai";

    public LlmProvider Provider => LlmProvider.GoogleAIStudio;

    public ChatOptions BuildChatOptions(LlmConfiguration config, string? jsonSchema = null)
    {
        var options = new ChatOptions
        {
            Temperature = (float)(config.Temperature > 0 ? config.Temperature : 1.0f),
            MaxOutputTokens = config.MaxTokens,
        };

        // Google uses response_mime_type for JSON output — our client detects ResponseFormat on ChatOptions.
        if (!string.IsNullOrWhiteSpace(jsonSchema))
        {
            var jsonEl = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(jsonSchema);
            options.ResponseFormat = ChatResponseFormat.ForJsonSchema(jsonEl, "structured_output", "");
        }

        return options;
    }

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGeneration(LlmConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            throw new InvalidOperationException("Google AI Studio requires an API key.");

        var embeddingModel = config.EmbeddingModelName ?? "text-embedding-004";
        return OpenAIProviderHelpers.CreateOpenAIClient(OpenAICompatEndpoint, config.ApiKey)
            .GetEmbeddingClient(embeddingModel)
            .AsIEmbeddingGenerator();
    }
}
