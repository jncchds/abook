#pragma warning disable SKEXP0070, SKEXP0010

using ABook.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;

namespace ABook.Infrastructure.Llm.Strategies;

public class GoogleAIStudioProviderStrategy : ILlmProviderStrategy
{
    // Google's OpenAI-compatible endpoint for embeddings (supported since mid-2024).
    private const string OpenAICompatEndpoint = "https://generativelanguage.googleapis.com/v1beta/openai";

    public LlmProvider Provider => LlmProvider.GoogleAIStudio;

    public IChatCompletionService CreateChatCompletion(LlmConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            throw new InvalidOperationException("Google AI Studio requires an API key.");
        return new GoogleAIGeminiChatCompletionService(config.ModelName, config.ApiKey);
    }

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGeneration(LlmConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            throw new InvalidOperationException("Google AI Studio requires an API key.");
        var embeddingModel = config.EmbeddingModelName ?? config.ModelName;
        // Use Google's OpenAI-compatible layer; text-embedding-004 is the recommended model.
        return OpenAIProviderHelpers.CreateOpenAIClient(OpenAICompatEndpoint, config.ApiKey)
            .GetEmbeddingClient(embeddingModel)
            .AsIEmbeddingGenerator();
    }

    public void ConfigureKernelBuilder(IKernelBuilder builder, LlmConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            throw new InvalidOperationException("Google AI Studio requires an API key.");
        builder.AddGoogleAIGeminiChatCompletion(config.ModelName, config.ApiKey);
        if (!string.IsNullOrWhiteSpace(config.EmbeddingModelName))
            builder.AddOpenAIEmbeddingGenerator(
                config.EmbeddingModelName,
                OpenAIProviderHelpers.CreateOpenAIClient(OpenAICompatEndpoint, config.ApiKey));
    }

    public PromptExecutionSettings CreateExecutionSettings(float temperature, bool jsonMode = false) =>
        new GeminiPromptExecutionSettings
        {
            Temperature = (double?)temperature,
            ResponseMimeType = jsonMode ? "application/json" : null,
        };
}
