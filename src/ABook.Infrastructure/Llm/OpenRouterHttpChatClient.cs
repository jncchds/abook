using ABook.Core.Models;
using Microsoft.Extensions.AI;
using System.Text;
using System.Text.Json;

namespace ABook.Infrastructure.Llm;

/// <summary>
/// Custom IChatClient for OpenAI-compatible endpoints that bypasses MEAI's SSE parser.
/// MEAI/OpenAI SDK silently drops streaming chunks where delta.content is empty but
/// delta.reasoning has text (e.g. Nemotron, GPT-OSS on OpenRouter). This client parses
/// SSE directly so both content and reasoning tokens are surfaced through MEAI's standard channel:
/// content via ChatResponseUpdate.Text, thinking via AdditionalProperties["ReasoningContent"].
/// Agents only use streaming, so non-streaming paths return a simple response.
/// </summary>
public class OpenRouterHttpChatClient : IChatClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly LlmConfiguration _config;
    private readonly string _model;
    private readonly string? _apiKey;

    public OpenRouterHttpChatClient(LlmConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            throw new InvalidOperationException("API key is required for OpenAI-compatible endpoints.");

        _config = config;
        _model = config.ModelName;
        _apiKey = config.ApiKey!;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(config.TimeoutMs ?? 120_000)
        };
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
        => StreamOpenAIAsync(messages, options, cancellationToken);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
    {
        var url = BuildUrl();
        var body = BuildRequestBody(messages, options, stream: false);
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(_apiKey))
            request.Headers.Add("Authorization", $"Bearer {_apiKey}");
        request.Headers.Add("User-Agent", "ABook/1.0");

        return _httpClient.SendAsync(request, cancellationToken).ContinueWith(t =>
        {
            if (t.IsFaulted) throw t.Exception!.InnerException ?? t.Exception;
            var resp = t.Result;
            resp.EnsureSuccessStatusCode();
            var json = resp.Content.ReadAsStringAsync().Result;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? textContent = null;
            string? reasoningText = null;
            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("message", out var msg))
                {
                    if (msg.TryGetProperty("content", out var contentEl)) textContent = contentEl.GetString();
                    if (msg.TryGetProperty("reasoning", out var reasonEl)) reasoningText = reasonEl.GetString();
                }
            }
            var chatMsg = new ChatMessage(ChatRole.Assistant, textContent ?? "");
            return new ChatResponse(chatMsg) { RawRepresentation = reasoningText };
        }, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() => _httpClient.Dispose();

    private async IAsyncEnumerable<ChatResponseUpdate> StreamOpenAIAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var url = BuildUrl();
        var body = BuildRequestBody(messages, options, stream: true);
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(_apiKey))
            request.Headers.Add("Authorization", $"Bearer {_apiKey}");
        request.Headers.Add("User-Agent", "ABook/1.0");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string accumulated = "";

        while (await reader.ReadLineAsync() is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (!line.StartsWith("data:")) continue;
            var json = line[5..].Trim();
            if (string.IsNullOrWhiteSpace(json)) continue;

            accumulated += json;
            while (accumulated.Length > 0)
            {
                int closeBrace = FindTopLevelCloseBrace(accumulated);
                if (closeBrace <= 0) break;
                var segment = accumulated[..(closeBrace + 1)];
                accumulated = accumulated[(closeBrace + 1)..];
                foreach (var update in SafeProcessSegment(segment))
                    yield return update;
            }
        }
    }

    private static int FindTopLevelCloseBrace(string text)
    {
        int depth = 0, i = 0;
        bool inString = false, escape = false;
        while (i < text.Length)
        {
            var c = text[i++];
            if (escape) { escape = false; continue; }
            if (c == '\\') { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '{') depth++;
            else if (c == '}') { depth--; if (depth == 0) return i - 1; }
        }
        return -1;
    }

    private static List<ChatResponseUpdate> SafeProcessSegment(string segment)
    {
        var results = new List<ChatResponseUpdate>();
        try
        {
            using var doc = JsonDocument.Parse(segment);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices)) return results;
            if (choices.GetArrayLength() == 0) return results;
            var choice = choices[0];
            if (!choice.TryGetProperty("delta", out var delta)) return results;

            var reasoning = delta.TryGetProperty("reasoning", out var rEl) && !rEl.ValueKind.Equals(System.Text.Json.JsonValueKind.Null) ? rEl.GetString() : null;
            var content = delta.TryGetProperty("content", out var cEl) && !cEl.ValueKind.Equals(System.Text.Json.JsonValueKind.Null) ? cEl.GetString() : null;

            // Surface reasoning via AdditionalProperties so AgentBase extracts it into its
            // separate thinking accumulator without polluting the content stream used for JSON parsing.
            // Reasoning-only chunks (empty content) are skipped to prevent mixed blob output.
            if (!string.IsNullOrEmpty(reasoning))
            {
                var update = new ChatResponseUpdate();
                update.Role = ChatRole.Assistant;
                update.AdditionalProperties = new AdditionalPropertiesDictionary();
                update.AdditionalProperties["ReasoningContent"] = reasoning!;
                results.Add(update);
            }

            if (!string.IsNullOrEmpty(content))
                results.Add(new ChatResponseUpdate(ChatRole.Assistant, content!));
        }
        catch (JsonException) { /* skip malformed */ }
        return results;
    }

    private string BuildUrl() => _config.Endpoint!.TrimEnd('/') + "/chat/completions";

    private string BuildRequestBody(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["messages"] = ConvertMessages(messages),
            ["stream"] = stream,
        };
        if (options?.Temperature is > 0 and <= 2.0f) body["temperature"] = options.Temperature;
        if (options?.MaxOutputTokens.HasValue == true && options.MaxOutputTokens.Value > 0) body["max_tokens"] = options.MaxOutputTokens.Value;
        if (options?.ResponseFormat is ChatResponseFormatJson { Schema: not null } jsonFmt)
        {
            var jsonEl = System.Text.Json.JsonSerializer.SerializeToElement(jsonFmt.Schema);
            body["response_format"] = new { type = "json_schema", json_schema = new { name = jsonFmt.SchemaName ?? "structured_output", schema = jsonEl, strict = true } };
        }
        if (!string.IsNullOrWhiteSpace(options?.Reasoning?.Effort.ToString()) && options.Reasoning.Effort != ReasoningEffort.None)
            body["extra_body"] = new { reasoning_effort = options.Reasoning.Effort.ToString().ToLower() };
        return JsonSerializer.Serialize(body);
    }

    private List<object> ConvertMessages(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages)
    {
        var result = new List<object>();
        foreach (var msg in messages)
        {
            if (msg.Role == Microsoft.Extensions.AI.ChatRole.System) continue;
            result.Add(new Dictionary<string, object?> { ["role"] = MapRole(msg.Role), ["content"] = msg.Text ?? "" });
        }
        return result;
    }

    private static string MapRole(Microsoft.Extensions.AI.ChatRole role) =>
        role == Microsoft.Extensions.AI.ChatRole.User ? "user" :
        role == Microsoft.Extensions.AI.ChatRole.Assistant ? "assistant" : "system";
}
