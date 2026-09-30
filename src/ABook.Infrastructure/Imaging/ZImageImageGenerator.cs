using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using ABook.Infrastructure.Llm;

namespace ABook.Infrastructure.Imaging;

/// <summary>
/// Client for the self-hosted Z-Image API (FastAPI over DiffSynth's ZImagePipeline; see adate's
/// <c>compose/zimage</c>). <c>POST /generate</c> holds the connection for the whole render and returns an
/// image id, which is then fetched from <c>GET /images/{id}</c>. The server has no authentication.
/// </summary>
public sealed class ZImageImageGenerator : IImageGenerator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly Func<TimeSpan, Uri, HttpClient> _clientFactory;

    public ZImageImageGenerator() : this((timeout, baseAddress) => LlmHttp.CreateClient(timeout, baseAddress)) { }

    /// <summary>Test seam: lets tests supply a client over a fake handler.</summary>
    internal ZImageImageGenerator(Func<TimeSpan, Uri, HttpClient> clientFactory) => _clientFactory = clientFactory;

    public async Task<GeneratedImage> GenerateAsync(ImageGenRequest request, IImageGenSettings settings, CancellationToken ct = default)
    {
        using var http = CreateClient(settings);
        var body = BuildRequest(request, settings);

        // A sized body, not PostAsJsonAsync's chunked stream — not every server behind a proxy accepts chunked uploads.
        using var content = new StringContent(JsonSerializer.Serialize(body, Json), System.Text.Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("generate", content, ct);
        await EnsureSuccessAsync(response, "generate", ct);
        var generated = await response.Content.ReadFromJsonAsync<GenerateResponse>(Json, ct)
            ?? throw new InvalidOperationException("Z-Image returned an empty /generate response.");
        if (string.IsNullOrWhiteSpace(generated.ImageId))
            throw new InvalidOperationException("Z-Image /generate response carried no image_id.");

        using var image = await http.GetAsync($"images/{Uri.EscapeDataString(generated.ImageId)}", ct);
        await EnsureSuccessAsync(image, "images", ct);
        var bytes = await image.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length < PngSignature.Length || !bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
            throw new InvalidOperationException($"Z-Image returned {bytes.Length} bytes that are not a PNG image.");

        return new GeneratedImage(bytes, "image/png", generated.Seed ?? request.Seed);
    }

    public async Task<ImageGenHealth> CheckHealthAsync(IImageGenSettings settings, CancellationToken ct = default)
    {
        using var http = CreateClient(settings, TimeSpan.FromSeconds(15));
        using var response = await http.GetAsync("health", ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            return new ImageGenHealth(false, $"HTTP {(int)response.StatusCode}", Truncate(text));
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "unknown" : "unknown";
            var ready = root.TryGetProperty("pipeline_ready", out var r) && r.ValueKind == JsonValueKind.True;
            var model = root.TryGetProperty("model", out var m) ? m.GetString() : null;
            return new ImageGenHealth(ready, status, model);
        }
        catch (JsonException)
        {
            return new ImageGenHealth(false, "unrecognised response", Truncate(text));
        }
    }

    internal static GenerateRequest BuildRequest(ImageGenRequest request, IImageGenSettings settings) => new(
        Prompt: request.Prompt,
        NegativePrompt: request.NegativePrompt ?? string.Empty,
        Seed: request.Seed,
        NumInferenceSteps: settings.ImageSteps is > 0 ? settings.ImageSteps.Value : ImageGenDefaults.Steps,
        CfgScale: settings.ImageCfg is > 0 ? settings.ImageCfg.Value : ImageGenDefaults.Cfg,
        Width: request.Width,
        Height: request.Height);

    private HttpClient CreateClient(IImageGenSettings settings, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(settings.ImageEndpoint))
            throw new InvalidOperationException("No image endpoint is configured.");
        var endpoint = settings.ImageEndpoint.Trim();
        if (!endpoint.EndsWith('/')) endpoint += "/";
        var ms = settings.ImageTimeoutMs is > 0 ? settings.ImageTimeoutMs.Value : ImageGenDefaults.TimeoutMs;
        return _clientFactory(timeout ?? TimeSpan.FromMilliseconds(ms), new Uri(endpoint));
    }

    /// <summary>FastAPI puts the useful detail in the body, so it goes into the exception message.</summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string call, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        string detail = body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("detail", out var d))
                detail = d.ValueKind == JsonValueKind.String ? d.GetString() ?? body : d.GetRawText();
        }
        catch (JsonException) { }
        throw new HttpRequestException(
            $"Z-Image /{call} failed with HTTP {(int)response.StatusCode}: {Truncate(detail)}", null, response.StatusCode);
    }

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500] + "…";

    internal sealed record GenerateRequest(
        string Prompt, string NegativePrompt, long Seed, int NumInferenceSteps, double CfgScale, int Width, int Height);

    private sealed record GenerateResponse(string? ImageId, long? Seed);
}

public sealed class ImageGeneratorFactory : IImageGeneratorFactory
{
    private readonly ZImageImageGenerator _zImage = new();

    public IImageGenerator Create(ImageProvider? provider) => provider switch
    {
        null or ImageProvider.ZImage => _zImage,
        _ => throw new NotSupportedException($"Image provider {provider} is not supported."),
    };
}

/// <summary>Ollama unloads a model when asked to generate with <c>keep_alive: 0</c>.</summary>
public sealed class LlmModelUnloader : ILlmModelUnloader
{
    public async Task<bool> UnloadAsync(LlmConfiguration config, CancellationToken ct = default)
    {
        if (config.Provider != LlmProvider.Ollama || string.IsNullOrWhiteSpace(config.Endpoint)) return false;
        var endpoint = config.Endpoint.Trim().TrimEnd('/');
        // The configured endpoint may be Ollama's OpenAI-compatible /v1 root.
        if (endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) endpoint = endpoint[..^3];
        using var http = LlmHttp.CreateClient(TimeSpan.FromSeconds(60), new Uri(endpoint + "/"));
        using var content = new StringContent(
            JsonSerializer.Serialize(new { model = config.ModelName, keep_alive = 0 }), System.Text.Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("api/generate", content, ct);
        response.EnsureSuccessStatusCode();
        return true;
    }
}
