using ABook.Core.Interfaces;
using ABook.Core.Models;
using ABook.Infrastructure.Llm.Strategies;
using Microsoft.Extensions.AI;

namespace ABook.Infrastructure.Llm;

/// <summary>
/// Factory that creates MEAI chat clients and embedding generators based on the configured LLM provider.
/// Chat clients use custom HttpClients with configurable timeouts for Ollama and Google.
/// OpenAI uses the official MEAI.OpenAI connector which provides automatic ReasoningContent extraction.
/// Embedding generation continues using the existing OpenAI-compatible endpoint pattern for all providers.
/// </summary>
public class LlmProviderFactory : ILlmProviderFactory
{
    private static readonly Dictionary<LlmProvider, IProviderConfigMapper> Mappers = new(
        new IProviderConfigMapper[]
        {
            new OllamaProviderConfigMapper(),
            new OpenAIProviderConfigMapper(),
            new OpenAICompatibleProviderConfigMapper(),
            new GoogleAiStudioProviderConfigMapper(),
        }.ToDictionary(s => s.Provider));

    private static IProviderConfigMapper GetMapper(LlmProvider provider) =>
        Mappers.TryGetValue(provider, out var mapper) ? mapper : throw new NotSupportedException($"Provider '{provider}' is not supported.");

    public IChatClient CreateChatClient(LlmConfiguration config) => config.Provider switch
    {
        LlmProvider.Ollama => new OllamaChatClient(config),
        LlmProvider.OpenAI => new OpenAiChatClient(config),
        LlmProvider.OpenAICompatible => new OpenRouterHttpChatClient(config),
        LlmProvider.GoogleAIStudio => new GoogleAiStudioChatClient(config),
        _ => throw new NotSupportedException($"Provider '{config.Provider}' is not supported."),
    };

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGeneration(LlmConfiguration config) =>
        GetMapper(config.Provider).CreateEmbeddingGeneration(config);

    public ChatOptions BuildChatOptions(LlmConfiguration config, string? jsonSchema = null) =>
        GetMapper(config.Provider).BuildChatOptions(config, jsonSchema);
}
