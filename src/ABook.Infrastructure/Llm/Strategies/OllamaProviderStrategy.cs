#pragma warning disable SKEXP0070

using ABook.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;
using OllamaSharp;

namespace ABook.Infrastructure.Llm.Strategies;

public class OllamaProviderStrategy : ILlmProviderStrategy
{
    public LlmProvider Provider => LlmProvider.Ollama;

    public IChatCompletionService CreateChatCompletion(LlmConfiguration config) =>
        new OllamaApiClient(new Uri(config.Endpoint), config.ModelName).AsChatCompletionService();

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGeneration(LlmConfiguration config)
    {
        var embeddingModel = config.EmbeddingModelName ?? config.ModelName;
        // Use Ollama's OpenAI-compatible embedding endpoint (supported since 0.4.x).
        return OpenAIProviderHelpers.CreateOpenAIClient(config.Endpoint.TrimEnd('/') + "/v1", "ollama")
            .GetEmbeddingClient(embeddingModel)
            .AsIEmbeddingGenerator();
    }

    public void ConfigureKernelBuilder(IKernelBuilder builder, LlmConfiguration config)
    {
        builder.AddOllamaChatCompletion(config.ModelName, new Uri(config.Endpoint));
        var embeddingModel = config.EmbeddingModelName ?? config.ModelName;
        builder.AddOllamaEmbeddingGenerator(embeddingModel, new Uri(config.Endpoint));
    }

    // The SK 1.74.0-alpha Ollama connector does not expose Ollama's `format: "json"` API parameter
    // via OllamaPromptExecutionSettings. JSON output quality is ensured by the prompt text alone.
    public PromptExecutionSettings CreateExecutionSettings(float temperature, bool jsonMode = false) =>
        new OllamaPromptExecutionSettings { Temperature = (float?)temperature };
}
