using ABook.Core.Models;
using Microsoft.Extensions.AI;

namespace ABook.Infrastructure.Llm.Strategies;

public class OllamaProviderConfigMapper : IProviderConfigMapper
{
    public LlmProvider Provider => LlmProvider.Ollama;

    public ChatOptions BuildChatOptions(LlmConfiguration config, string? jsonSchema = null)
    {
        var options = new ChatOptions
        {
            Temperature = (float)(config.Temperature > 0 ? config.Temperature : 0.7f),
            MaxOutputTokens = config.MaxTokens,
        };

        // OllamaSharp maps its own option keys via AbstractionMapper.TryAddOllamaOption internally.
        return options;
    }

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGeneration(LlmConfiguration config)
    {
        var embeddingModel = config.EmbeddingModelName ?? config.ModelName;
        // Use Ollama's OpenAI-compatible embedding endpoint (supported since 0.4.x).
        return OpenAIProviderHelpers.CreateOpenAIClient(
            config.Endpoint.TrimEnd('/') + "/v1", "ollama")
            .GetEmbeddingClient(embeddingModel)
            .AsIEmbeddingGenerator();
    }
}
