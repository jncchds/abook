using ABook.Core.Interfaces;
using ABook.Core.Models;
using ABook.Infrastructure.Llm.Strategies;
using Microsoft.Extensions.AI;

namespace ABook.Infrastructure.Llm;

public class LlmProviderFactory : ILlmProviderFactory
{
    private static readonly Dictionary<LlmProvider, ILlmProviderStrategy> Strategies =
        new ILlmProviderStrategy[]
        {
            new OllamaProviderStrategy(),
            new OpenAIProviderStrategy(),
            new GoogleAIStudioProviderStrategy(),
            new OpenAICompatibleProviderStrategy(),
        }.ToDictionary(s => s.Provider);

    private static ILlmProviderStrategy GetStrategy(LlmProvider provider) =>
        Strategies.TryGetValue(provider, out var strategy)
            ? strategy
            : throw new NotSupportedException($"Provider '{provider}' is not supported.");

    public ILlmChatClient CreateChatClient(LlmConfiguration config) =>
        GetStrategy(config.Provider).CreateChatClient(config);

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGeneration(LlmConfiguration config) =>
        GetStrategy(config.Provider).CreateEmbeddingGeneration(config);
}
