#pragma warning disable SKEXP0070, SKEXP0010

using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;
using Microsoft.SemanticKernel.Embeddings;
using OpenAI;
using System.ClientModel;

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
                config.ModelName, new Uri(config.Endpoint.TrimEnd('/') + "/v1"), config.ApiKey ?? "lm-studio"),
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
                embeddingModel, new OpenAIClient(
                    new ApiKeyCredential(config.ApiKey ?? "lm-studio"),
                    new OpenAIClientOptions { Endpoint = new Uri(config.Endpoint.TrimEnd('/') + "/v1") })),
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
                builder.AddOpenAIChatCompletion(config.ModelName, lmEndpoint, lmKey);
                if (!string.IsNullOrWhiteSpace(config.EmbeddingModelName))
                {
                    var lmClient = new OpenAIClient(
                        new ApiKeyCredential(lmKey),
                        new OpenAIClientOptions { Endpoint = lmEndpoint });
                    builder.AddOpenAITextEmbeddingGeneration(config.EmbeddingModelName, lmClient);
                }
                break;
            default:
                throw new NotSupportedException($"Provider {config.Provider} is not yet supported.");
        }

        return builder.Build();
    }
}
