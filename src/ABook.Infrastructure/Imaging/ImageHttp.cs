using System.Net.Http.Headers;
using System.Text.Json;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using ABook.Infrastructure.Llm;

namespace ABook.Infrastructure.Imaging;

/// <summary>HTTP plumbing shared by the image-server clients.</summary>
internal static class ImageHttp
{
    internal static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    internal static readonly Func<TimeSpan, Uri, HttpClient> DefaultClientFactory =
        (timeout, baseAddress) => LlmHttp.CreateClient(timeout, baseAddress);

    /// <summary>
    /// A client rooted at <paramref name="endpoint"/> (with a trailing slash, so relative paths keep any proxy
    /// prefix), timing out after the configured per-image timeout unless <paramref name="timeout"/> overrides it.
    /// </summary>
    internal static HttpClient CreateClient(
        Func<TimeSpan, Uri, HttpClient> factory, string endpoint, IImageGenSettings settings, TimeSpan? timeout = null)
    {
        if (!endpoint.EndsWith('/')) endpoint += "/";
        var ms = settings.ImageTimeoutMs is > 0 ? settings.ImageTimeoutMs.Value : ImageGenDefaults.TimeoutMs;
        var http = factory(timeout ?? TimeSpan.FromMilliseconds(ms), new Uri(endpoint));
        if (!string.IsNullOrWhiteSpace(settings.ImageApiKey))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ImageApiKey.Trim());
        return http;
    }

    internal static string RequireEndpoint(IImageGenSettings settings) =>
        string.IsNullOrWhiteSpace(settings.ImageEndpoint)
            ? throw new InvalidOperationException("No image endpoint is configured.")
            : settings.ImageEndpoint.Trim();

    /// <summary>A sized body, not PostAsJsonAsync's chunked stream — not every server behind a proxy accepts chunked uploads.</summary>
    internal static StringContent JsonContent<T>(T body, JsonSerializerOptions options) =>
        new(JsonSerializer.Serialize(body, options), System.Text.Encoding.UTF8, "application/json");

    internal static void EnsurePng(byte[] bytes, string server)
    {
        if (bytes.Length < PngSignature.Length || !bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
            throw new InvalidOperationException($"{server} returned {bytes.Length} bytes that are not a PNG image.");
    }

    /// <summary>FastAPI puts the useful detail in the body, so it goes into the exception message.</summary>
    internal static async Task EnsureSuccessAsync(HttpResponseMessage response, string server, string call, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(
            $"{server} /{call} failed with HTTP {(int)response.StatusCode}: {Truncate(ErrorDetail(body))}", null, response.StatusCode);
    }

    /// <summary>
    /// FastAPI's <c>detail</c>: a string, or an object such as OpenAI's <c>{"error":{"message":…}}</c>
    /// envelope, which Unsloth Studio uses on its OpenAI-shaped routes. Falls back to the raw body.
    /// </summary>
    internal static string ErrorDetail(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("detail", out var d))
                return body;
            if (d.ValueKind == JsonValueKind.String) return d.GetString() ?? body;
            if (d.ValueKind == JsonValueKind.Object)
            {
                if (d.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) return m.GetString()!;
                if (d.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object
                    && e.TryGetProperty("message", out var em) && em.ValueKind == JsonValueKind.String)
                    return em.GetString()!;
            }
            return d.GetRawText();
        }
        catch (JsonException)
        {
            return body;
        }
    }

    internal static string Truncate(string text) => text.Length <= 500 ? text : text[..500] + "…";
}
