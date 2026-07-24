using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.AI;

namespace ABook.Infrastructure.Llm.Strategies;

/// <summary>
/// OpenAI-compatible endpoints (LMStudio, OpenRouter, vLLM, etc.).
/// Uses a raw SSE reader so that non-standard delta fields like
/// <c>reasoning_content</c> (OpenRouter/DeepSeek-R1) are captured alongside content.
/// Does NOT send <c>reasoning_effort</c> — most compat endpoints do not support it.
/// </summary>
public class OpenAICompatibleProviderStrategy : ILlmProviderStrategy
{
    public LlmProvider Provider => LlmProvider.OpenAICompatible;

    public ILlmChatClient CreateChatClient(LlmConfiguration config) =>
        new OpenAICompatibleChatClient(config);

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGeneration(LlmConfiguration config)
    {
        var embeddingModel = config.EmbeddingModelName ?? config.ModelName;
        return OpenAIProviderHelpers.CreateOpenAIClient(config.Endpoint, config.ApiKey)
            .GetEmbeddingClient(embeddingModel)
            .AsIEmbeddingGenerator();
    }

    private sealed class OpenAICompatibleChatClient(LlmConfiguration config) : ILlmChatClient
    {
        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
            IReadOnlyList<LlmChatMessage> messages,
            LlmChatOptions options,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMilliseconds(options.TimeoutMs ?? config.TimeoutMs ?? 300_000),
            };

            if (!string.IsNullOrWhiteSpace(config.ApiKey))
                httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.ApiKey);

            var baseUrl = config.Endpoint.TrimEnd('/');
            // Some compat servers already include /v1 in the endpoint, some don't.
            var chatUrl = baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
                ? baseUrl
                : baseUrl + "/chat/completions";

            object? responseFormat = null;
            if (!string.IsNullOrWhiteSpace(options.JsonSchema))
            {
                responseFormat = new
                {
                    type = "json_schema",
                    json_schema = new
                    {
                        name = "structured_output",
                        schema = JsonSerializer.Deserialize<JsonElement>(options.JsonSchema),
                        strict = true,
                    },
                };
            }

            var requestBody = new
            {
                model = config.ModelName,
                stream = true,
                messages = messages.Select(m => new
                {
                    role = m.Role switch
                    {
                        LlmChatRole.System => "system",
                        LlmChatRole.Assistant => "assistant",
                        _ => "user",
                    },
                    content = m.Content,
                }).ToArray(),
                temperature = options.Temperature is { } t && t > 0 ? (float?)t : null,
                max_tokens = options.MaxTokens is { } mx && mx > 0 ? (int?)mx : null,
                response_format = responseFormat,
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, chatUrl)
            {
                Content = JsonContent.Create(requestBody, options: _jsonOpts),
            };

            using var response = await httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new System.IO.StreamReader(stream);

            string? line;
            while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

                var json = line["data: ".Length..];
                if (json == "[DONE]") break;

                LlmStreamChunk? chunk = null;
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("choices", out var choices)) continue;
                    if (choices.GetArrayLength() == 0) continue;
                    var delta = choices[0].GetProperty("delta");

                    string? content = null;
                    string? reasoning = null;

                    if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                        content = c.GetString();
                    if (delta.TryGetProperty("reasoning_content", out var r) && r.ValueKind == JsonValueKind.String)
                        reasoning = r.GetString();

                    if (content?.Length > 0 || reasoning?.Length > 0)
                        chunk = new LlmStreamChunk(content, reasoning);
                }
                catch (JsonException)
                {
                    // Skip malformed SSE events
                }

                if (chunk is not null)
                    yield return chunk;
            }
        }
    }
}
