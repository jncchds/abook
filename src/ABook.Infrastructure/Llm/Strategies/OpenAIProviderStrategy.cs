#pragma warning disable SKEXP0010

using ABook.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenAI;
using System.ClientModel;

namespace ABook.Infrastructure.Llm.Strategies;

public class OpenAIProviderStrategy : ILlmProviderStrategy
{
    public LlmProvider Provider => LlmProvider.OpenAI;

    public IChatCompletionService CreateChatCompletion(LlmConfiguration config)
    {
        var openAiEndpoint = string.IsNullOrWhiteSpace(config.Endpoint) ? null : new Uri(config.Endpoint);
        if (openAiEndpoint == null && string.IsNullOrWhiteSpace(config.ApiKey))
            throw new InvalidOperationException("OpenAI API key is required when no custom endpoint is set.");
        return new Microsoft.SemanticKernel.Connectors.OpenAI.OpenAIChatCompletionService(
            config.ModelName, openAiEndpoint!, config.ApiKey);
    }

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGeneration(LlmConfiguration config)
    {
        var embeddingModel = config.EmbeddingModelName ?? config.ModelName;
        if (string.IsNullOrWhiteSpace(config.Endpoint) && string.IsNullOrWhiteSpace(config.ApiKey))
            throw new InvalidOperationException("OpenAI API key is required when no custom endpoint is set.");
        if (string.IsNullOrWhiteSpace(config.Endpoint))
            return new OpenAIClient(new ApiKeyCredential(config.ApiKey ?? ""))
                .GetEmbeddingClient(embeddingModel)
                .AsIEmbeddingGenerator();
        return OpenAIProviderHelpers.CreateOpenAIClient(config.Endpoint, config.ApiKey)
            .GetEmbeddingClient(embeddingModel)
            .AsIEmbeddingGenerator();
    }

    public void ConfigureKernelBuilder(IKernelBuilder builder, LlmConfiguration config)
    {
        var openAiEndpoint = string.IsNullOrWhiteSpace(config.Endpoint) ? null : new Uri(config.Endpoint);
        var apiKey = openAiEndpoint != null ? config.ApiKey : (config.ApiKey ?? throw new InvalidOperationException("OpenAI API key is required when no custom endpoint is set."));
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

    public PromptExecutionSettings CreateExecutionSettings(LlmConfiguration config, string? jsonSchema = null)
    {
        var settings = new OpenAIPromptExecutionSettings
        {
            Temperature = (double?)(config.Temperature > 0 ? config.Temperature : null),
            ResponseFormat = !string.IsNullOrWhiteSpace(jsonSchema)
                ? OpenAI.Chat.ChatResponseFormat.CreateJsonSchemaFormat("structured_output", BinaryData.FromString(jsonSchema!))
                : null,
        };

        if (config.MaxTokens.HasValue && config.MaxTokens.Value > 0)
            settings.MaxTokens = config.MaxTokens.Value;
        if (!string.IsNullOrWhiteSpace(config.ReasoningEffort) && config.ReasoningEffort != "none")
            settings.ReasoningEffort = config.ReasoningEffort;

        return settings;
    }
}
