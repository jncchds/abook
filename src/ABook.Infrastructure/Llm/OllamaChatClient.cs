using ABook.Core.Models;
using Microsoft.Extensions.AI;
using OllamaSharp;

namespace ABook.Infrastructure.Llm;

/// <summary>
/// Wraps OllamaSharp's native <see cref="OllamaApiClient"/> (which implements <see cref="IChatClient"/>).
/// Passes a custom HttpClient so per-config timeouts are applied directly.
/// </summary>
public class OllamaChatClient : IChatClient
{
    private readonly OllamaApiClient _inner;

    public OllamaChatClient(LlmConfiguration config)
    {
        var httpClient = new HttpClient
        {
            BaseAddress = new Uri(config.Endpoint),
            Timeout = TimeSpan.FromMilliseconds(config.TimeoutMs ?? 120_000)
        };
        _inner = new OllamaApiClient(httpClient, config.ModelName);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken) =>
        ((IChatClient)_inner).GetStreamingResponseAsync(messages, options, cancellationToken);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken) =>
        ((IChatClient)_inner).GetResponseAsync(messages, options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() => _inner.Dispose();
}
