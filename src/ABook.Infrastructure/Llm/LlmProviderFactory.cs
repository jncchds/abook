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
                    embeddingModel, new OpenAIClient(
                        new ApiKeyCredential("ollama"),
                        new OpenAIClientOptions { Endpoint = new Uri(config.Endpoint.TrimEnd('/') + "/v1") }));
            case LlmProvider.OpenAI:
            {
                if (string.IsNullOrWhiteSpace(config.Endpoint))
                    return new Microsoft.SemanticKernel.Connectors.OpenAI.OpenAITextEmbeddingGenerationService(
                        embeddingModel, config.ApiKey ?? throw new InvalidOperationException("OpenAI requires an API key."));
                var oaiClient = new OpenAIClient(
                    new ApiKeyCredential(config.ApiKey ?? ""),
                    new OpenAIClientOptions { Endpoint = new Uri(config.Endpoint.TrimEnd('/')) });
                return new Microsoft.SemanticKernel.Connectors.OpenAI.OpenAITextEmbeddingGenerationService(
                    embeddingModel, oaiClient);
            }
            case LlmProvider.LMStudio:
                return new Microsoft.SemanticKernel.Connectors.OpenAI.OpenAITextEmbeddingGenerationService(
                    embeddingModel, new OpenAIClient(
                        new ApiKeyCredential(config.ApiKey ?? "lm-studio"),
                        new OpenAIClientOptions { Endpoint = new Uri(config.Endpoint.TrimEnd('/') + "/v1") }));
            case LlmProvider.Anthropic:
            {
                if (string.IsNullOrWhiteSpace(config.EmbeddingModelName))
                    throw new NotSupportedException("Anthropic requires an EmbeddingModelName to generate embeddings.");
                if (string.IsNullOrWhiteSpace(config.Endpoint))
                    throw new InvalidOperationException(
                        "Anthropic requires an endpoint (e.g. an OpenAI-compatible proxy like LiteLLM).");
                var anthropicClient = new OpenAIClient(
                    new ApiKeyCredential(config.ApiKey ?? ""),
                    new OpenAIClientOptions { Endpoint = new Uri(config.Endpoint.TrimEnd('/')) });
                return new Microsoft.SemanticKernel.Connectors.OpenAI.OpenAITextEmbeddingGenerationService(
                    embeddingModel, anthropicClient);
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
                    {
                        var oaiClient = new OpenAIClient(
                            new ApiKeyCredential(apiKey ?? ""),
                            new OpenAIClientOptions { Endpoint = new Uri(config.Endpoint.TrimEnd('/')) });
                        builder.AddOpenAITextEmbeddingGeneration(config.EmbeddingModelName, oaiClient);
                    }
                }
                break;
            }
            case LlmProvider.LMStudio:
            {
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
            }
            case LlmProvider.Anthropic:
            {
                // Anthropic's native API is not OpenAI-compatible; use an OpenAI-compatible proxy
                // (e.g. LiteLLM at http://localhost:4000) and set the endpoint accordingly.
                if (string.IsNullOrWhiteSpace(config.Endpoint))
                    throw new InvalidOperationException(
                        "Anthropic requires an endpoint (e.g. an OpenAI-compatible proxy like LiteLLM).");
                var anthropicEndpoint = new Uri(config.Endpoint);
                builder.AddOpenAIChatCompletion(config.ModelName, anthropicEndpoint, config.ApiKey);
                if (!string.IsNullOrWhiteSpace(config.EmbeddingModelName))
                {
                    var anthropicClient = new OpenAIClient(
                        new ApiKeyCredential(config.ApiKey ?? ""),
                        new OpenAIClientOptions { Endpoint = new Uri(config.Endpoint.TrimEnd('/')) });
                    builder.AddOpenAITextEmbeddingGeneration(config.EmbeddingModelName, anthropicClient);
                }
                break;
            }
            default:
                throw new NotSupportedException($"Provider {config.Provider} is not yet supported.");
        }

        return builder.Build();
    }
}
