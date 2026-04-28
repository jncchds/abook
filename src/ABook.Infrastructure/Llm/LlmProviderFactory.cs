using ABook.Core.Interfaces;
using ABook.Core.Models;
using ABook.Infrastructure.Llm.Strategies;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ABook.Infrastructure.Llm;

public class LlmProviderFactory : ILlmProviderFactory
{
    // TODO: delete AzureOpenAI — never implemented
    private static readonly Dictionary<LlmProvider, ILlmProviderStrategy> Strategies =
        new ILlmProviderStrategy[]
        {
            new OllamaProviderStrategy(),
            new OpenAIProviderStrategy(),
            new AnthropicProviderStrategy(),
            new GoogleAIStudioProviderStrategy(),
        }.ToDictionary(s => s.Provider);

    private static ILlmProviderStrategy GetStrategy(LlmProvider provider) =>
        Strategies.TryGetValue(provider, out var strategy)
            ? strategy
            : throw new NotSupportedException($"Provider '{provider}' is not supported.");

    public IChatCompletionService CreateChatCompletion(LlmConfiguration config) =>
        GetStrategy(config.Provider).CreateChatCompletion(config);

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGeneration(LlmConfiguration config) =>
        GetStrategy(config.Provider).CreateEmbeddingGeneration(config);

    public Kernel CreateKernel(LlmConfiguration config)
    {
        var builder = Kernel.CreateBuilder();
        GetStrategy(config.Provider).ConfigureKernelBuilder(builder, config);
        return builder.Build();
    }

    public PromptExecutionSettings CreateExecutionSettings(LlmConfiguration config, float temperature, bool jsonMode = false) =>
        GetStrategy(config.Provider).CreateExecutionSettings(temperature, jsonMode);
}
