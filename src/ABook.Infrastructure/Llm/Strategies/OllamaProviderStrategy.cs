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

    public IChatCompletionService CreateChatCompletion(LlmConfiguration config)
    {
        var httpClient = new HttpClient
        {
            BaseAddress = new Uri(config.Endpoint),
            Timeout = TimeSpan.FromMilliseconds(config.TimeoutMs ?? 120000)
        };
        // OllamaApiClient owns the HttpClient for its lifetime; dispose when SK service is disposed.
        return new OllamaApiClient(httpClient, config.ModelName).AsChatCompletionService();
    }

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
    public PromptExecutionSettings CreateExecutionSettings(LlmConfiguration config, string? jsonSchema = null)
    {
        var settings = new OllamaPromptExecutionSettings
        {
            Temperature = (float?)(config.Temperature > 0 ? config.Temperature : null),
            NumPredict = config.MaxTokens,
            ExtensionData = new Dictionary<string, object>(),
        };

        if (config.TimeoutMs.HasValue && config.TimeoutMs.Value > 0)
            settings.ExtensionData["timeout"] = config.TimeoutMs.Value;
        if (!string.IsNullOrWhiteSpace(config.ReasoningEffort))
            settings.ExtensionData["reasoning_effort"] = config.ReasoningEffort;

        return settings;
    }
}
