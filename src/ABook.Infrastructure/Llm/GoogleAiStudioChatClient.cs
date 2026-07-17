using ABook.Core.Models;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace ABook.Infrastructure.Llm;

/// <summary>
/// Custom <see cref="IChatClient"/> implementation for Google AI Studio (Gemini).
/// Calls the Gemini streaming REST API directly via HttpClient, mapping SSE chunks
/// to MEAI's <see cref="ChatResponseUpdate"/>. Yields updates as they arrive.
/// </summary>
public class GoogleAiStudioChatClient : IChatClient, IDisposable
{
    private const string GeminiEndpoint = "https://generativelanguage.googleapis.com/v1beta/models";

    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly string _apiKey;
    private bool _disposed;

    public GoogleAiStudioChatClient(LlmConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            throw new InvalidOperationException("Google AI Studio requires an API key.");

        _model = config.ModelName;
        _apiKey = config.ApiKey!;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(config.TimeoutMs ?? 120_000)
        };
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
        => StreamAsync(messages, options, cancellationToken);

    private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var url = $"{GeminiEndpoint}/{_model}:streamGenerateContent?key={_apiKey}";

        // Map MEAI ChatMessage → Gemini parts format
        var contents = new List<JsonElement>();
        foreach (var msg in messages)
        {
            var role = MapRole(msg.Role);
            var text = msg.Text ?? "";
            var parts = new List<object> { new { text } };
            contents.Add(JsonSerializer.SerializeToElement(new { role, parts }));
        }

        // Build generation config from ChatOptions
        var generationConfig = BuildGenerationConfig(options);

        var bodyStr = JsonSerializer.Serialize(new
        {
            contents,
            generationConfig
        });

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(bodyStr, Encoding.UTF8, "application/json")
        };

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string accumulated = "";

        while (await reader.ReadLineAsync() is { } line)
        {
            ct.ThrowIfCancellationRequested();

            if (!line.StartsWith("data:")) continue;
            var json = line.Substring(5).Trim();
            if (string.IsNullOrWhiteSpace(json)) continue;

            accumulated += json;

            while (accumulated.Length > 0)
            {
                int depth = 0;
                bool inString = false;
                bool escape = false;
                int? start = null;

                for (int i = 0; i < accumulated.Length; i++)
                {
                    var c = accumulated[i];
                    if (escape) { escape = false; continue; }
                    if (c == '\\') { escape = true; continue; }
                    if (c == '"') { inString = !inString; continue; }
                    if (inString) continue;

                    if (c == '{' && depth == 0) start = i;
                    else if (c == '{') depth++;
                    else if (c == '}' && depth > 0) depth--;
                    else if (c == '}' && depth == 0 && start.HasValue)
                    {
                        var segment = accumulated[start.Value..(i + 1)];
                        accumulated = accumulated[(i + 1)..];

                        // Process outside try/catch to allow yield
                        foreach (var update in SafeProcessSegment(segment))
                            yield return update;

                        break;
                    }
                }

                if (start.HasValue && depth == 0) continue;
                if (!start.HasValue || depth > 0) break;
            }
        }
    }

    private static List<ChatResponseUpdate> SafeProcessSegment(string segment)
    {
        var results = new List<ChatResponseUpdate>();
        try
        {
            using var doc = JsonDocument.Parse(segment);
            var root = doc.RootElement;

            if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
            {
                var candidate = candidates[0];
                if (candidate.TryGetProperty("content", out var content))
                {
                    if (content.TryGetProperty("parts", out var parts) && parts.GetArrayLength() > 0)
                    {
                        var part = parts[0];
                        if (part.TryGetProperty("text", out var textEl) && textEl.GetString() is string text)
                        {
                            // Use string literal to disambiguate between (ChatRole?, string?) and (ChatRole?, IList<AIContent>?) overloads.
                            results.Add(new ChatResponseUpdate(ChatRole.Assistant, new string(text)));
                        }

                        // Extended thinking models (Gemini 2.0+) expose reasoning via "thinking" field.
                        if (part.TryGetProperty("thinking", out var thinkingEl) && thinkingEl.GetString() is string thinkingText)
                        {
                            // Disambiguate by typing the second argument explicitly
                            string? contentStr = null;
                            var update = new ChatResponseUpdate(ChatRole.Assistant, contentStr);
                            update.AdditionalProperties.Add("ReasoningContent", thinkingText);
                            results.Add(update);
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Skip malformed segments; tag-based thinking fallback in AgentBase handles content extraction.
        }

        return results;
    }

    private static string MapRole(Microsoft.Extensions.AI.ChatRole role)
    {
        if (role == Microsoft.Extensions.AI.ChatRole.User) return "user";
        return "model"; // system and assistant both map to "model" in Gemini
    }

    private static Dictionary<string, object?> BuildGenerationConfig(ChatOptions? options)
    {
        var config = new Dictionary<string, object?>();
        if (options?.Temperature is > 0 and <= 2.0f)
            config["temperature"] = options.Temperature;
        if (options?.MaxOutputTokens.HasValue == true && options.MaxOutputTokens.Value > 0)
            config["maxOutputTokens"] = options.MaxOutputTokens.Value;

        // Google uses response_mime_type for JSON output — detected via ResponseFormatJson on ChatOptions.
        if (options?.ResponseFormat is ChatResponseFormatJson { Schema: not null })
            config["responseMimeType"] = "application/json";

        return config;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
    {
        var url = $"{GeminiEndpoint}/{_model}:generateContent?key={_apiKey}";

        var contents = new List<JsonElement>();
        foreach (var msg in messages)
        {
            var role = MapRole(msg.Role);
            var text = msg.Text ?? "";
            var parts = new List<object> { new { text } };
            contents.Add(JsonSerializer.SerializeToElement(new { role, parts }));
        }

        var generationConfig = BuildGenerationConfig(options);
        var body = JsonSerializer.SerializeToElement(new { contents, generationConfig });

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json")
        };

        return _httpClient.SendAsync(request, cancellationToken).ContinueWith(t =>
        {
            if (t.IsFaulted) throw t.Exception!.InnerException ?? t.Exception;
            var response = t.Result;
            response.EnsureSuccessStatusCode();
            var content = response.Content.ReadAsStringAsync().Result;
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            string? textContent = null;
            string? reasoningText = null;

            if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
            {
                var candidate = candidates[0];
                if (candidate.TryGetProperty("content", out var contentEl))
                {
                    if (contentEl.TryGetProperty("parts", out var parts) && parts.GetArrayLength() > 0)
                    {
                        var part = parts[0];
                        if (part.TryGetProperty("text", out var textEl))
                            textContent = textEl.GetString();

                        if (part.TryGetProperty("thinking", out var thinkingEl))
                            reasoningText = thinkingEl.GetString();
                    }
                }
            }

            var chatMsg = new ChatMessage(ChatRole.Assistant, textContent ?? "");
            return new ChatResponse(chatMsg) { RawRepresentation = reasoningText };
        }, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient.Dispose();
            _disposed = true;
        }
    }
}
