using OpenAI;
using System.ClientModel;

namespace ABook.Infrastructure.Llm;

internal static class OpenAIProviderHelpers
{
    internal static OpenAIClient CreateOpenAIClient(string endpoint, string? apiKey) =>
        new(new ApiKeyCredential(apiKey ?? ""),
            new OpenAIClientOptions { Endpoint = new Uri(endpoint.TrimEnd('/')) });
}
