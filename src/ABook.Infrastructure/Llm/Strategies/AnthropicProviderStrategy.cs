#pragma warning disable SKEXP0010

using ABook.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ABook.Infrastructure.Llm.Strategies;

public class AnthropicProviderStrategy : ILlmProviderStrategy
{
    public LlmProvider Provider => LlmProvider.Anthropic;

    public IChatCompletionService CreateChatCompletion(LlmConfiguration config)
    {
        // Anthropic's native API is not OpenAI-compatible; use an OpenAI-compatible proxy
        // (e.g. LiteLLM at http://localhost:4000) and set the endpoint accordingly.
        if (string.IsNullOrWhiteSpace(config.Endpoint))
            throw new InvalidOperationException(
                "Anthropic requires an endpoint (e.g. an OpenAI-compatible proxy like LiteLLM).");
        return new Microsoft.SemanticKernel.Connectors.OpenAI.OpenAIChatCompletionService(
            config.ModelName, new Uri(config.Endpoint), config.ApiKey);
    }

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGeneration(LlmConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.EmbeddingModelName))
            throw new NotSupportedException("Anthropic requires an EmbeddingModelName to generate embeddings.");
        if (string.IsNullOrWhiteSpace(config.Endpoint))
            throw new InvalidOperationException(
                "Anthropic requires an endpoint (e.g. an OpenAI-compatible proxy like LiteLLM).");
        return OpenAIProviderHelpers.CreateOpenAIClient(config.Endpoint, config.ApiKey)
            .GetEmbeddingClient(config.EmbeddingModelName)
            .AsIEmbeddingGenerator();
    }

    public void ConfigureKernelBuilder(IKernelBuilder builder, LlmConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.Endpoint))
            throw new InvalidOperationException(
                "Anthropic requires an endpoint (e.g. an OpenAI-compatible proxy like LiteLLM).");
        builder.AddOpenAIChatCompletion(config.ModelName, new Uri(config.Endpoint), config.ApiKey);
        if (!string.IsNullOrWhiteSpace(config.EmbeddingModelName))
            builder.AddOpenAIEmbeddingGenerator(
                config.EmbeddingModelName, OpenAIProviderHelpers.CreateOpenAIClient(config.Endpoint, config.ApiKey));
    }
}
