#pragma warning disable SKEXP0010

using ABook.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using OpenAI;
using System.ClientModel;

namespace ABook.Infrastructure.Llm.Strategies;

public class OpenAIProviderStrategy : ILlmProviderStrategy
{
    public LlmProvider Provider => LlmProvider.OpenAI;

    public IChatCompletionService CreateChatCompletion(LlmConfiguration config)
    {
        var openAiEndpoint = string.IsNullOrWhiteSpace(config.Endpoint) ? null : new Uri(config.Endpoint);
        if (openAiEndpoint == null)
            return new Microsoft.SemanticKernel.Connectors.OpenAI.OpenAIChatCompletionService(
                config.ModelName, config.ApiKey ?? throw new InvalidOperationException("OpenAI requires an API key."));
        return new Microsoft.SemanticKernel.Connectors.OpenAI.OpenAIChatCompletionService(
            config.ModelName, openAiEndpoint, config.ApiKey);
    }

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGeneration(LlmConfiguration config)
    {
        var embeddingModel = config.EmbeddingModelName ?? config.ModelName;
        if (string.IsNullOrWhiteSpace(config.Endpoint))
            return new OpenAIClient(new ApiKeyCredential(
                    config.ApiKey ?? throw new InvalidOperationException("OpenAI requires an API key.")))
                .GetEmbeddingClient(embeddingModel)
                .AsIEmbeddingGenerator();
        return OpenAIProviderHelpers.CreateOpenAIClient(config.Endpoint, config.ApiKey)
            .GetEmbeddingClient(embeddingModel)
            .AsIEmbeddingGenerator();
    }

    public void ConfigureKernelBuilder(IKernelBuilder builder, LlmConfiguration config)
    {
        var openAiEndpoint = string.IsNullOrWhiteSpace(config.Endpoint) ? null : new Uri(config.Endpoint);
        var apiKey = config.ApiKey ?? (openAiEndpoint == null
            ? throw new InvalidOperationException("OpenAI requires an API key.")
            : null);
        if (openAiEndpoint == null)
        {
            builder.AddOpenAIChatCompletion(config.ModelName, apiKey!);
            builder.AddOpenAIEmbeddingGenerator(
                config.EmbeddingModelName ?? "text-embedding-3-small", apiKey!);
        }
        else
        {
            builder.AddOpenAIChatCompletion(config.ModelName, openAiEndpoint, apiKey);
            if (!string.IsNullOrWhiteSpace(config.EmbeddingModelName))
                builder.AddOpenAIEmbeddingGenerator(
                    config.EmbeddingModelName, OpenAIProviderHelpers.CreateOpenAIClient(config.Endpoint, apiKey));
        }
    }
}
