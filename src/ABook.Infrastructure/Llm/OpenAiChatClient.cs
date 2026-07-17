using ABook.Core.Models;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;

namespace ABook.Infrastructure.Llm;

/// <summary>
/// Wraps the OpenAI SDK's <see cref="ChatClient"/> via MEAI.OpenAI's <c>AsIChatClient()</c> extension.
/// </summary>
public class OpenAiChatClient : IChatClient
{
    private readonly IChatClient _inner;

    public OpenAiChatClient(LlmConfiguration config)
    {
        var openaiEndpoint = string.IsNullOrWhiteSpace(config.Endpoint) ? null : new Uri(config.Endpoint);
        if (openaiEndpoint == null && string.IsNullOrWhiteSpace(config.ApiKey))
            throw new InvalidOperationException("OpenAI API key is required when no custom endpoint is set.");

        ChatClient chatClient;
        if (openaiEndpoint != null)
        {
            var options = new OpenAIClientOptions { Endpoint = openaiEndpoint };
            var client = new OpenAIClient(new ApiKeyCredential(config.ApiKey ?? ""), options);
            chatClient = client.GetChatClient(config.ModelName);
        }
        else
        {
            // Standard OpenAI API — model + apiKey constructor handles auth internally.
            chatClient = new ChatClient(config.ModelName, config.ApiKey ?? "");
        }

        _inner = chatClient.AsIChatClient();
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken) =>
        ((IChatClient)_inner).GetStreamingResponseAsync(messages, options, cancellationToken);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken) =>
        ((IChatClient)_inner).GetResponseAsync(messages, options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) => _inner.GetService(serviceType);

    public void Dispose()
    {
        if (_inner is IDisposable disposable) disposable.Dispose();
    }
}
