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
    /// <summary>
    /// Build an <see cref="OpenAIClient"/> pointed at a custom endpoint.
    /// <paramref name="apiKey"/> may be null/empty for proxies that don't require authentication.
    /// </summary>
    private static OpenAIClient CreateOpenAIClient(string endpoint, string? apiKey) =>
        new(new ApiKeyCredential(apiKey ?? ""),
            new OpenAIClientOptions { Endpoint = new Uri(endpoint.TrimEnd('/')) });

    public IChatCompletionService CreateChatCompletion(LlmConfiguration config)
    {
        switch (config.Provider)
        {
            case LlmProvider.Ollama:
                return new OllamaChatCompletionService(config.ModelName, new Uri(config.Endpoint));
            case LlmProvider.OpenAI:
            {
                var openAiEndpoint = string.IsNullOrWhiteSpace(config.Endpoint) ? null : new Uri(config.Endpoint);
                if (openAiEndpoint == null)
                    return new Microsoft.SemanticKernel.Connectors.OpenAI.OpenAIChatCompletionService(
                        config.ModelName, config.ApiKey ?? throw new InvalidOperationException("OpenAI requires an API key."));
                return new Microsoft.SemanticKernel.Connectors.OpenAI.OpenAIChatCompletionService(
                    config.ModelName, openAiEndpoint, config.ApiKey);
            }
            case LlmProvider.LMStudio:
                return new Microsoft.SemanticKernel.Connectors.OpenAI.OpenAIChatCompletionService(
                    config.ModelName, new Uri(config.Endpoint.TrimEnd('/') + "/v1"), config.ApiKey ?? "lm-studio");
            case LlmProvider.Anthropic:
            {
                // Anthropic's native API is not OpenAI-compatible; use an OpenAI-compatible proxy
                // (e.g. LiteLLM at http://localhost:4000) and set the endpoint accordingly.
                if (string.IsNullOrWhiteSpace(config.Endpoint))
                    throw new InvalidOperationException(
                        "Anthropic requires an endpoint (e.g. an OpenAI-compatible proxy like LiteLLM).");
                return new Microsoft.SemanticKernel.Connectors.OpenAI.OpenAIChatCompletionService(
                    config.ModelName, new Uri(config.Endpoint), config.ApiKey);
            }
            default:
                throw new NotSupportedException($"Provider {config.Provider} is not yet supported.");
        }
    }

    public ITextEmbeddingGenerationService CreateEmbeddingGeneration(LlmConfiguration config)
    {
        var embeddingModel = config.EmbeddingModelName ?? config.ModelName;
        switch (config.Provider)
        {
            case LlmProvider.Ollama:
                // OllamaTextEmbeddingGenerationService is broken in SK 1.74.0-alpha (internal cast fails).
                // Use the OpenAI-compatible embedding endpoint that Ollama has supported since 0.4.x.
                return new Microsoft.SemanticKernel.Connectors.OpenAI.OpenAITextEmbeddingGenerationService(
                    embeddingModel, CreateOpenAIClient(config.Endpoint.TrimEnd('/') + "/v1", "ollama"));
            case LlmProvider.OpenAI:
            {
                if (string.IsNullOrWhiteSpace(config.Endpoint))
                    return new Microsoft.SemanticKernel.Connectors.OpenAI.OpenAITextEmbeddingGenerationService(
                        embeddingModel, config.ApiKey ?? throw new InvalidOperationException("OpenAI requires an API key."));
                return new Microsoft.SemanticKernel.Connectors.OpenAI.OpenAITextEmbeddingGenerationService(
                    embeddingModel, CreateOpenAIClient(config.Endpoint, config.ApiKey));
            }
            case LlmProvider.LMStudio:
                return new Microsoft.SemanticKernel.Connectors.OpenAI.OpenAITextEmbeddingGenerationService(
                    embeddingModel, CreateOpenAIClient(config.Endpoint.TrimEnd('/') + "/v1", config.ApiKey ?? "lm-studio"));
            case LlmProvider.Anthropic:
            {
                if (string.IsNullOrWhiteSpace(config.EmbeddingModelName))
                    throw new NotSupportedException("Anthropic requires an EmbeddingModelName to generate embeddings.");
                if (string.IsNullOrWhiteSpace(config.Endpoint))
                    throw new InvalidOperationException(
                        "Anthropic requires an endpoint (e.g. an OpenAI-compatible proxy like LiteLLM).");
                return new Microsoft.SemanticKernel.Connectors.OpenAI.OpenAITextEmbeddingGenerationService(
                    embeddingModel, CreateOpenAIClient(config.Endpoint, config.ApiKey));
            }
            default:
                throw new NotSupportedException($"Provider {config.Provider} is not yet supported for embeddings.");
        }
    }

    public Kernel CreateKernel(LlmConfiguration config)
    {
        var builder = Kernel.CreateBuilder();

        switch (config.Provider)
        {
            case LlmProvider.Ollama:
            {
                builder.AddOllamaChatCompletion(config.ModelName, new Uri(config.Endpoint));
                var embeddingModel = config.EmbeddingModelName ?? config.ModelName;
                builder.AddOllamaTextEmbeddingGeneration(embeddingModel, new Uri(config.Endpoint));
                break;
            }
            case LlmProvider.OpenAI:
            {
                var openAiEndpoint = string.IsNullOrWhiteSpace(config.Endpoint) ? null : new Uri(config.Endpoint);
                var apiKey = config.ApiKey ?? (openAiEndpoint == null
                    ? throw new InvalidOperationException("OpenAI requires an API key.")
                    : null);
                if (openAiEndpoint == null)
                {
                    builder.AddOpenAIChatCompletion(config.ModelName, apiKey!);
                    builder.AddOpenAITextEmbeddingGeneration(
                        config.EmbeddingModelName ?? "text-embedding-3-small", apiKey!);
                }
                else
                {
                    builder.AddOpenAIChatCompletion(config.ModelName, openAiEndpoint, apiKey);
                    if (!string.IsNullOrWhiteSpace(config.EmbeddingModelName))
                        builder.AddOpenAITextEmbeddingGeneration(
                            config.EmbeddingModelName, CreateOpenAIClient(config.Endpoint, apiKey));
                }
                break;
            }
            case LlmProvider.LMStudio:
            {
                var lmKey = config.ApiKey ?? "lm-studio";
                var lmEndpoint = new Uri(config.Endpoint.TrimEnd('/') + "/v1");
                builder.AddOpenAIChatCompletion(config.ModelName, lmEndpoint, lmKey);
                if (!string.IsNullOrWhiteSpace(config.EmbeddingModelName))
                    builder.AddOpenAITextEmbeddingGeneration(
                        config.EmbeddingModelName, CreateOpenAIClient(config.Endpoint.TrimEnd('/') + "/v1", lmKey));
                break;
            }
            case LlmProvider.Anthropic:
            {
                // Anthropic's native API is not OpenAI-compatible; use an OpenAI-compatible proxy
                // (e.g. LiteLLM at http://localhost:4000) and set the endpoint accordingly.
                if (string.IsNullOrWhiteSpace(config.Endpoint))
                    throw new InvalidOperationException(
                        "Anthropic requires an endpoint (e.g. an OpenAI-compatible proxy like LiteLLM).");
                builder.AddOpenAIChatCompletion(config.ModelName, new Uri(config.Endpoint), config.ApiKey);
                if (!string.IsNullOrWhiteSpace(config.EmbeddingModelName))
                    builder.AddOpenAITextEmbeddingGeneration(
                        config.EmbeddingModelName, CreateOpenAIClient(config.Endpoint, config.ApiKey));
                break;
            }
            default:
                throw new NotSupportedException($"Provider {config.Provider} is not yet supported.");
        }

        return builder.Build();
    }
}
