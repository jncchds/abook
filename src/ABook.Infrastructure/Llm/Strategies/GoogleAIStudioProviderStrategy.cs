using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.AI;

namespace ABook.Infrastructure.Llm.Strategies;

/// <summary>
/// Google AI Studio through Gemini's own API (<c>models/{model}:streamGenerateContent</c>, server-sent events), read
/// directly: thought summaries come back as reasoning (saved as "💭 Thinking"), Reasoning Effort sets the thinking
/// budget, structured output uses <c>responseJsonSchema</c>, and a reply Gemini blocks or cuts off says why instead
/// of ending empty. Embeddings still go through Google's OpenAI-compatible endpoint.
/// </summary>
public class GoogleAIStudioProviderStrategy : ILlmProviderStrategy
{
    internal const string DefaultEndpoint = "https://generativelanguage.googleapis.com/v1beta";

    public LlmProvider Provider => LlmProvider.GoogleAIStudio;

    public ILlmChatClient CreateChatClient(LlmConfiguration config) =>
        new GeminiChatClient(config);

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGeneration(LlmConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            throw new InvalidOperationException("Google AI Studio requires an API key.");
        var embeddingModel = config.EmbeddingModelName ?? config.ModelName;
        return OpenAIProviderHelpers.CreateOpenAIClient(BaseUrl(config.Endpoint) + "/openai", config.ApiKey, config.TimeoutMs)
            .GetEmbeddingClient(embeddingModel)
            .AsIEmbeddingGenerator();
    }

    /// <summary>
    /// The API's base URL. Settings saved before the native API was used point at the OpenAI-compatible endpoint
    /// (<c>…/v1beta/openai</c>): its <c>/openai</c> is dropped.
    /// </summary>
    internal static string BaseUrl(string? endpoint)
    {
        var url = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint.Trim().TrimEnd('/');
        return url.EndsWith("/openai", StringComparison.OrdinalIgnoreCase) ? url[..^"/openai".Length] : url;
    }

    /// <summary>Gemini models that think: 2.5 and later (not 1.x, 2.0 or Gemma).</summary>
    internal static bool Thinks(string model)
    {
        var m = ModelId(model).ToLowerInvariant();
        return m.StartsWith("gemini-", StringComparison.Ordinal)
               && !m.StartsWith("gemini-1", StringComparison.Ordinal)
               && !m.StartsWith("gemini-2.0", StringComparison.Ordinal);
    }

    internal static string ModelId(string model) => model.StartsWith("models/", StringComparison.Ordinal) ? model["models/".Length..] : model;

    /// <summary>
    /// The thinking budget for a Reasoning Effort: blank = the model's own (dynamic) choice; "none" turns thinking off
    /// (Pro models cannot, so they get the smallest budget). Null when nothing is set.
    /// </summary>
    internal static int? ThinkingBudget(string model, string? effort) => effort?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "none" => ModelId(model).Contains("pro", StringComparison.OrdinalIgnoreCase) ? 128 : 0,
        "low" => 1024,
        "medium" => 8192,
        _ => 24576,
    };

    /// <summary>
    /// The generateContent body: system messages become the system instruction, assistant turns the "model" role.
    /// <paramref name="thinking"/> and <paramref name="schema"/> can be left out for a model that refuses them.
    /// </summary>
    internal static JsonObject BuildRequest(
        IReadOnlyList<LlmChatMessage> messages, LlmChatOptions options, string model, bool thinking = true, bool schema = true)
    {
        var system = string.Join("\n\n", messages.Where(m => m.Role == LlmChatRole.System).Select(m => m.Content));
        var contents = new JsonArray();
        foreach (var m in messages.Where(m => m.Role != LlmChatRole.System))
            contents.Add(Content(m.Role == LlmChatRole.Assistant ? "model" : "user", m.Content));
        // Gemini needs at least one turn: a system-only request is sent as the user's turn.
        if (contents.Count == 0)
        {
            contents.Add(Content("user", system));
            system = "";
        }

        var body = new JsonObject { ["contents"] = contents };
        if (system.Length > 0)
            body["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) };

        var config = new JsonObject();
        if (options.Temperature is { } t && t > 0) config["temperature"] = t;
        if (options.MaxTokens is { } max && max > 0) config["maxOutputTokens"] = max;
        if (!string.IsNullOrWhiteSpace(options.JsonSchema))
        {
            config["responseMimeType"] = "application/json";
            if (schema) config["responseJsonSchema"] = JsonNode.Parse(options.JsonSchema);
        }
        if (thinking && Thinks(model))
        {
            var budget = ThinkingBudget(model, options.ReasoningEffort);
            var thinkingConfig = new JsonObject { ["includeThoughts"] = budget != 0 };
            if (budget is int b) thinkingConfig["thinkingBudget"] = b;
            config["thinkingConfig"] = thinkingConfig;
        }
        if (config.Count > 0) body["generationConfig"] = config;
        return body;

        static JsonObject Content(string role, string text) =>
            new() { ["role"] = role, ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) };
    }

    /// <summary>
    /// One streamed event: its text and thought-summary parts, and — when Gemini blocked the prompt or stopped the
    /// reply for a reason other than finishing or the token limit — why.
    /// </summary>
    internal static (List<LlmStreamChunk> Chunks, string? Stopped) ParseEvent(string json)
    {
        var chunks = new List<LlmStreamChunk>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("promptFeedback", out var feedback)
            && feedback.TryGetProperty("blockReason", out var block) && block.GetString() is { Length: > 0 } blockReason)
            return (chunks, $"Gemini blocked the request ({blockReason}).");
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array
            || candidates.GetArrayLength() == 0)
            return (chunks, null);

        var candidate = candidates[0];
        if (candidate.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts)
            && parts.ValueKind == JsonValueKind.Array)
            foreach (var part in parts.EnumerateArray())
            {
                if (!part.TryGetProperty("text", out var text) || text.GetString() is not { Length: > 0 } s) continue;
                var thought = part.TryGetProperty("thought", out var t) && t.ValueKind == JsonValueKind.True;
                chunks.Add(thought ? new LlmStreamChunk(null, s) : new LlmStreamChunk(s, null));
            }

        var finish = candidate.TryGetProperty("finishReason", out var f) ? f.GetString() : null;
        var stopped = finish is null or "" or "STOP" or "MAX_TOKENS" or "FINISH_REASON_UNSPECIFIED"
            ? null
            : $"Gemini stopped the reply ({finish}).";
        return (chunks, stopped);
    }

    /// <summary>Google's error message from an error body ({"error": {"message", "status"}}), or the raw text.</summary>
    internal static string ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m))
                return m.GetString() ?? body;
        }
        catch (JsonException) { }
        return body.Length > 500 ? body[..500] : body;
    }

    private sealed class GeminiChatClient(LlmConfiguration config) : ILlmChatClient
    {
        public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
            IReadOnlyList<LlmChatMessage> messages,
            LlmChatOptions options,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(config.ApiKey))
                throw new InvalidOperationException("Google AI Studio requires an API key.");

            using var http = LlmHttp.CreateClient(TimeSpan.FromMilliseconds(options.TimeoutMs ?? config.TimeoutMs ?? 300_000));
            using var response = await SendAsync(http, messages, options, ct);
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                List<LlmStreamChunk> chunks;
                string? stopped;
                try { (chunks, stopped) = ParseEvent(line["data:".Length..].Trim()); }
                catch (JsonException) { continue; }   // a malformed event
                foreach (var chunk in chunks) yield return chunk;
                if (stopped is not null) throw new InvalidOperationException(stopped);
            }
        }

        /// <summary>
        /// Sends the request; when Gemini refuses the thinking settings or the schema (an older model, a schema it
        /// can't take), sends it again without them — the schema still asks for JSON.
        /// </summary>
        private async Task<HttpResponseMessage> SendAsync(
            HttpClient http, IReadOnlyList<LlmChatMessage> messages, LlmChatOptions options, CancellationToken ct)
        {
            var url = $"{BaseUrl(config.Endpoint)}/models/{Uri.EscapeDataString(ModelId(config.ModelName))}:streamGenerateContent?alt=sse";
            bool thinking = true, schema = true;
            while (true)
            {
                var body = BuildRequest(messages, options, config.ModelName, thinking, schema);
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
                };
                request.Headers.Add("x-goog-api-key", config.ApiKey);
                var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.IsSuccessStatusCode) return response;

                var error = ErrorMessage(await response.Content.ReadAsStringAsync(ct));
                var status = response.StatusCode;
                response.Dispose();
                if (status == HttpStatusCode.BadRequest && thinking && body["generationConfig"]?["thinkingConfig"] is not null
                    && error.Contains("think", StringComparison.OrdinalIgnoreCase))
                    thinking = false;
                else if (status == HttpStatusCode.BadRequest && schema && !string.IsNullOrWhiteSpace(options.JsonSchema)
                         && error.Contains("schema", StringComparison.OrdinalIgnoreCase))
                    schema = false;
                else
                    throw new HttpRequestException($"Google AI Studio: {error}", null, status);
            }
        }
    }
}
