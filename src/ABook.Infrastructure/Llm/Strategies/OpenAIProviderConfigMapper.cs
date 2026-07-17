using ABook.Core.Models;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;

namespace ABook.Infrastructure.Llm.Strategies;

public class OpenAIProviderConfigMapper : IProviderConfigMapper
{
    public LlmProvider Provider => LlmProvider.OpenAI;

    public ChatOptions BuildChatOptions(LlmConfiguration config, string? jsonSchema = null)
    {
        var options = new ChatOptions
        {
            Temperature = (float)(config.Temperature > 0 ? config.Temperature : 0.7f),
            MaxOutputTokens = config.MaxTokens,
        };

        if (!string.IsNullOrWhiteSpace(jsonSchema))
        {
            // OpenAI uses response_format with JSON schema
            var jsonEl = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(jsonSchema);
            options.ResponseFormat = ChatResponseFormat.ForJsonSchema(jsonEl, "structured_output", "");
        }

        // Map reasoning effort via ChatOptions.Reasoning (OpenAI supports this natively)
        if (!string.IsNullOrWhiteSpace(config.ReasoningEffort) && config.ReasoningEffort != "none")
        {
            options.Reasoning = new ReasoningOptions
            {
                Effort = Enum.Parse<ReasoningEffort>(config.ReasoningEffort, true)
            };
        }

        return options;
    }

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGeneration(LlmConfiguration config)
    {
        var embeddingModel = config.EmbeddingModelName ?? "text-embedding-3-small";
        if (string.IsNullOrWhiteSpace(config.Endpoint))
        {
            return new OpenAIClient(new ApiKeyCredential(config.ApiKey ?? ""))
                .GetEmbeddingClient(embeddingModel)
                .AsIEmbeddingGenerator();
        }

        return OpenAIProviderHelpers.CreateOpenAIClient(config.Endpoint, config.ApiKey)
            .GetEmbeddingClient(embeddingModel)
            .AsIEmbeddingGenerator();
    }
}
