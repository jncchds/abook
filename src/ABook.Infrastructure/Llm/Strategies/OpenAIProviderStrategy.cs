#pragma warning disable OPENAI001
using System.Runtime.CompilerServices;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using OAIChatMessage = OpenAI.Chat.ChatMessage;
using OAIResponseFormat = OpenAI.Chat.ChatResponseFormat;

namespace ABook.Infrastructure.Llm.Strategies;

public class OpenAIProviderStrategy : ILlmProviderStrategy
{
    public LlmProvider Provider => LlmProvider.OpenAI;

    public ILlmChatClient CreateChatClient(LlmConfiguration config) =>
        new OpenAIChatClientWrapper(config);

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGeneration(LlmConfiguration config)
    {
        var embeddingModel = config.EmbeddingModelName ?? config.ModelName;
        if (string.IsNullOrWhiteSpace(config.Endpoint))
            return new OpenAIClient(new ApiKeyCredential(config.ApiKey ?? ""))
                .GetEmbeddingClient(embeddingModel)
                .AsIEmbeddingGenerator();
        return OpenAIProviderHelpers.CreateOpenAIClient(config.Endpoint, config.ApiKey)
            .GetEmbeddingClient(embeddingModel)
            .AsIEmbeddingGenerator();
    }

    private sealed class OpenAIChatClientWrapper(LlmConfiguration config) : ILlmChatClient
    {
        public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
            IReadOnlyList<LlmChatMessage> messages,
            LlmChatOptions options,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            OpenAIClient openAiClient = string.IsNullOrWhiteSpace(config.Endpoint)
                ? new OpenAIClient(new ApiKeyCredential(config.ApiKey ?? ""))
                : OpenAIProviderHelpers.CreateOpenAIClient(config.Endpoint, config.ApiKey);

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
            if (!string.IsNullOrWhiteSpace(options.ReasoningEffort) && options.ReasoningEffort != "none")
                chatOptions.ReasoningEffortLevel = options.ReasoningEffort switch
                {
                    "low" => ChatReasoningEffortLevel.Low,
                    "medium" => ChatReasoningEffortLevel.Medium,
                    _ => ChatReasoningEffortLevel.High,
                };

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
