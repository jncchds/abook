using System.Runtime.CompilerServices;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using OAIChatMessage = OpenAI.Chat.ChatMessage;
using OAIResponseFormat = OpenAI.Chat.ChatResponseFormat;

namespace ABook.Infrastructure.Llm.Strategies;

public class GoogleAIStudioProviderStrategy : ILlmProviderStrategy
{
    // Google's OpenAI-compatible endpoint (used for both chat and embeddings).
    private const string DefaultEndpoint = "https://generativelanguage.googleapis.com/v1beta/openai";

    public LlmProvider Provider => LlmProvider.GoogleAIStudio;

    public ILlmChatClient CreateChatClient(LlmConfiguration config) =>
        new GoogleChatClient(config);

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGeneration(LlmConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            throw new InvalidOperationException("Google AI Studio requires an API key.");
        var embeddingModel = config.EmbeddingModelName ?? config.ModelName;
        var endpoint = string.IsNullOrWhiteSpace(config.Endpoint) ? DefaultEndpoint : config.Endpoint.TrimEnd('/');
        return OpenAIProviderHelpers.CreateOpenAIClient(endpoint, config.ApiKey)
            .GetEmbeddingClient(embeddingModel)
            .AsIEmbeddingGenerator();
    }

    private sealed class GoogleChatClient(LlmConfiguration config) : ILlmChatClient
    {
        public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
            IReadOnlyList<LlmChatMessage> messages,
            LlmChatOptions options,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(config.ApiKey))
                throw new InvalidOperationException("Google AI Studio requires an API key.");

            var endpoint = string.IsNullOrWhiteSpace(config.Endpoint) ? DefaultEndpoint : config.Endpoint.TrimEnd('/');
            var openAiClient = OpenAIProviderHelpers.CreateOpenAIClient(endpoint, config.ApiKey);
            var chatClient = openAiClient.GetChatClient(config.ModelName);

            var chatMessages = messages.Select<LlmChatMessage, OAIChatMessage>(m => m.Role switch
            {
                LlmChatRole.System => OAIChatMessage.CreateSystemMessage(m.Content),
                LlmChatRole.Assistant => OAIChatMessage.CreateAssistantMessage(m.Content),
                _ => OAIChatMessage.CreateUserMessage(m.Content),
            }).ToList();

            var chatOptions = new ChatCompletionOptions();
            if (options.Temperature is { } temp && temp > 0)
                chatOptions.Temperature = (float)temp;
            if (options.MaxTokens is { } max && max > 0)
                chatOptions.MaxOutputTokenCount = max;
            if (!string.IsNullOrWhiteSpace(options.JsonSchema))
                chatOptions.ResponseFormat = OAIResponseFormat.CreateJsonSchemaFormat(
                    "structured_output",
                    BinaryData.FromString(options.JsonSchema));

            await foreach (var update in chatClient.CompleteChatStreamingAsync(chatMessages, chatOptions, ct))
            {
                foreach (var part in update.ContentUpdate)
                {
                    if (part.Text?.Length > 0)
                        yield return new LlmStreamChunk(part.Text, null);
                }
            }
        }
    }
}
