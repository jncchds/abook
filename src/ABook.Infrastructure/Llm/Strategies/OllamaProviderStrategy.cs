using System.Runtime.CompilerServices;
using System.Text.Json;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OllamaSharp.Models.Chat;

namespace ABook.Infrastructure.Llm.Strategies;

public class OllamaProviderStrategy : ILlmProviderStrategy
{
    public LlmProvider Provider => LlmProvider.Ollama;

    public ILlmChatClient CreateChatClient(LlmConfiguration config) =>
        new OllamaChatClient(config);

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGeneration(LlmConfiguration config)
    {
        var embeddingModel = config.EmbeddingModelName ?? config.ModelName;
        return OpenAIProviderHelpers.CreateOpenAIClient(config.Endpoint.TrimEnd('/') + "/v1", "ollama")
            .GetEmbeddingClient(embeddingModel)
            .AsIEmbeddingGenerator();
    }

    private sealed class OllamaChatClient(LlmConfiguration config) : ILlmChatClient
    {
        public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
            IReadOnlyList<LlmChatMessage> messages,
            LlmChatOptions options,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var httpClient = new HttpClient
            {
                BaseAddress = new Uri(config.Endpoint),
                Timeout = TimeSpan.FromMilliseconds(options.TimeoutMs ?? config.TimeoutMs ?? 120_000),
            };

            var client = new OllamaApiClient(httpClient, config.ModelName);

            var thinkingEnabled = !string.IsNullOrWhiteSpace(options.ReasoningEffort)
                && options.ReasoningEffort != "none";

            var request = new ChatRequest
            {
                Model = config.ModelName,
                Stream = true,
                Think = thinkingEnabled ? true : null,
                Messages = messages.Select(m => new Message
                {
                    Role = m.Role switch
                    {
                        LlmChatRole.System => OllamaSharp.Models.Chat.ChatRole.System,
                        LlmChatRole.Assistant => OllamaSharp.Models.Chat.ChatRole.Assistant,
                        _ => OllamaSharp.Models.Chat.ChatRole.User,
                    },
                    Content = m.Content,
                }).ToList(),
                Options = new OllamaSharp.Models.RequestOptions
                {
                    Temperature = options.Temperature,
                    NumPredict = options.MaxTokens,
                },
            };

            if (!string.IsNullOrWhiteSpace(options.JsonSchema))
                request.Format = JsonSerializer.Deserialize<JsonElement>(options.JsonSchema);

            await foreach (var chunk in client.ChatAsync(request, ct))
            {
                if (chunk is null) continue;
                var content = chunk.Message?.Content;
                var reasoning = chunk.Message?.Thinking;
                if (content?.Length > 0 || reasoning?.Length > 0)
                    yield return new LlmStreamChunk(content, reasoning);
            }

            httpClient.Dispose();
        }
    }
}
