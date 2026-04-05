#pragma warning disable SKEXP0070

using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;
using Microsoft.SemanticKernel.Embeddings;

namespace ABook.Infrastructure.Llm;

public class LlmProviderFactory : ILlmProviderFactory
{
    public IChatCompletionService CreateChatCompletion(LlmConfiguration config) =>
        config.Provider switch
        {
            LlmProvider.Ollama => new OllamaChatCompletionService(config.ModelName, new Uri(config.Endpoint)),
            LlmProvider.OpenAI => new Microsoft.SemanticKernel.Connectors.OpenAI.OpenAIChatCompletionService(
                config.ModelName, config.ApiKey ?? throw new InvalidOperationException("OpenAI requires an API key.")),
            LlmProvider.LMStudio => new Microsoft.SemanticKernel.Connectors.OpenAI.OpenAIChatCompletionService(
                config.ModelName, config.ApiKey ?? "lm-studio",
                endpoint: new Uri(config.Endpoint.TrimEnd('/') + "/v1")),
            _ => throw new NotSupportedException($"Provider {config.Provider} is not yet supported.")
        };

    public ITextEmbeddingGenerationService CreateEmbeddingGeneration(LlmConfiguration config)
    {
        var embeddingModel = config.EmbeddingModelName ?? config.ModelName;
        return config.Provider switch
        {
            LlmProvider.Ollama => new OllamaTextEmbeddingGenerationService(embeddingModel, new Uri(config.Endpoint)),
            LlmProvider.OpenAI => new Microsoft.SemanticKernel.Connectors.OpenAI.OpenAITextEmbeddingGenerationService(
                embeddingModel, config.ApiKey ?? throw new InvalidOperationException("OpenAI requires an API key.")),
            LlmProvider.LMStudio => new Microsoft.SemanticKernel.Connectors.OpenAI.OpenAITextEmbeddingGenerationService(
                embeddingModel, config.ApiKey ?? "lm-studio",
                endpoint: new Uri(config.Endpoint.TrimEnd('/') + "/v1")),
            _ => throw new NotSupportedException($"Provider {config.Provider} is not yet supported for embeddings.")
        };
    }

    public Kernel CreateKernel(LlmConfiguration config)
    {
        var builder = Kernel.CreateBuilder();

        switch (config.Provider)
        {
            case LlmProvider.Ollama:
                builder.AddOllamaChatCompletion(config.ModelName, new Uri(config.Endpoint));
                var embeddingModel = config.EmbeddingModelName ?? config.ModelName;
                builder.AddOllamaTextEmbeddingGeneration(embeddingModel, new Uri(config.Endpoint));
                break;
            case LlmProvider.OpenAI:
                var apiKey = config.ApiKey ?? throw new InvalidOperationException("OpenAI requires an API key.");
                builder.AddOpenAIChatCompletion(config.ModelName, apiKey);
                builder.AddOpenAITextEmbeddingGeneration(config.EmbeddingModelName ?? "text-embedding-3-small", apiKey);
                break;
            case LlmProvider.LMStudio:
                var lmKey = config.ApiKey ?? "lm-studio";
                var lmEndpoint = new Uri(config.Endpoint.TrimEnd('/') + "/v1");
                builder.AddOpenAIChatCompletion(config.ModelName, lmKey, endpoint: lmEndpoint);
                if (!string.IsNullOrWhiteSpace(config.EmbeddingModelName))
                    builder.AddOpenAITextEmbeddingGeneration(config.EmbeddingModelName, lmKey, endpoint: lmEndpoint);
                break;
            default:
                throw new NotSupportedException($"Provider {config.Provider} is not yet supported.");
        }

        return builder.Build();
    }
}
