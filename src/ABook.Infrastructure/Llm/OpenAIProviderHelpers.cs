using OpenAI;
using System.ClientModel;

namespace ABook.Infrastructure.Llm;

internal static class OpenAIProviderHelpers
{
    /// <summary>
    /// Default per-operation network timeout, matching <see cref="Strategies.OpenAICompatibleProviderStrategy"/>.
    /// The SDK's own default is 100 seconds, which a streaming chapter or continuity pass routinely exceeds —
    /// leaving the run to die mid-stream with a bare TaskCanceledException.
    /// </summary>
    private static readonly TimeSpan DefaultNetworkTimeout = TimeSpan.FromMilliseconds(300_000);

    internal static OpenAIClient CreateOpenAIClient(string endpoint, string? apiKey, int? timeoutMs = null) =>
        new(new ApiKeyCredential(apiKey ?? ""),
            Options(timeoutMs, new Uri(endpoint.TrimEnd('/'))));

    /// <summary>Client options for the hosted OpenAI service, which needs no explicit endpoint.</summary>
    internal static OpenAIClientOptions Options(int? timeoutMs, Uri? endpoint = null)
    {
        var options = new OpenAIClientOptions
        {
            NetworkTimeout = timeoutMs is > 0 ? TimeSpan.FromMilliseconds(timeoutMs.Value) : DefaultNetworkTimeout,
        };
        if (endpoint is not null) options.Endpoint = endpoint;
        return options;
    }
}
