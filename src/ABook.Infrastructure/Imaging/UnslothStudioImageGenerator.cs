using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ABook.Core.Interfaces;
using ABook.Core.Models;

namespace ABook.Infrastructure.Imaging;

/// <summary>
/// Client for Unsloth Studio's native image API under <c>/api/inference/images</c>, authenticated with an
/// <c>sk-unsloth-…</c> API key. Studio's OpenAI-shaped <c>/v1/images/generations</c> is not used: it ignores the
/// seed, steps, guidance and negative prompt, and the seed is what keeps a character the same across images.
/// <para>
/// Studio keeps one heavy model on the GPU, so serving the text model from the same Studio evicts the image
/// model. When no image model is loaded and <see cref="IImageGenSettings.ImageModel"/> is set, the generator
/// loads it and waits; Studio evicts its chat model by itself to make room.
/// </para>
/// </summary>
public sealed class UnslothStudioImageGenerator : IImageGenerator
{
    private const string Server = "Unsloth Studio";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Studio's seed field is capped at 2^53 - 1 so it survives a JSON round trip.</summary>
    private const long MaxSeed = (1L << 53) - 1;

    /// <summary>A first load may download several GB; the run can still be cancelled while it waits.</summary>
    internal static TimeSpan LoadTimeout { get; set; } = TimeSpan.FromMinutes(30);
    internal static TimeSpan LoadPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    private readonly Func<TimeSpan, Uri, HttpClient> _clientFactory;

    public UnslothStudioImageGenerator() : this(ImageHttp.DefaultClientFactory) { }

    /// <summary>Test seam: lets tests supply a client over a fake handler.</summary>
    internal UnslothStudioImageGenerator(Func<TimeSpan, Uri, HttpClient> clientFactory) => _clientFactory = clientFactory;

    public async Task<GeneratedImage> GenerateAsync(ImageGenRequest request, IImageGenSettings settings, CancellationToken ct = default)
    {
        await EnsureModelLoadedAsync(settings, ct);

        using var http = CreateClient(settings);
        using var content = ImageHttp.JsonContent(BuildRequest(request, settings), Json);
        using var response = await http.PostAsync("api/inference/images/generate", content, ct);
        await ImageHttp.EnsureSuccessAsync(response, Server, "images/generate", ct);
        var generated = await response.Content.ReadFromJsonAsync<GenerateResponse>(Json, ct);
        var record = generated?.Images?.FirstOrDefault()
            ?? throw new InvalidOperationException("Unsloth Studio /images/generate returned no image.");
        if (string.IsNullOrWhiteSpace(record.Url))
            throw new InvalidOperationException("Unsloth Studio /images/generate returned an image without a URL.");

        // The URL is root-relative; dropping the leading slash keeps any path prefix of a reverse proxy.
        using var image = await http.GetAsync(record.Url.TrimStart('/'), ct);
        await ImageHttp.EnsureSuccessAsync(image, Server, "images/gallery", ct);
        var bytes = await image.Content.ReadAsByteArrayAsync(ct);
        ImageHttp.EnsurePng(bytes, Server);

        return new GeneratedImage(bytes, "image/png", record.Seed ?? request.Seed);
    }

    public async Task<ImageGenHealth> CheckHealthAsync(IImageGenSettings settings, CancellationToken ct = default)
    {
        using var http = CreateClient(settings, TimeSpan.FromSeconds(15));
        using var response = await http.GetAsync("api/inference/images/status", ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = ImageHttp.Truncate(ImageHttp.ErrorDetail(text));
            return response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? new ImageGenHealth(false, "unauthorized", $"Check the image API key. {detail}")
                : new ImageGenHealth(false, $"HTTP {(int)response.StatusCode}", detail);
        }

        StatusResponse? status;
        try { status = JsonSerializer.Deserialize<StatusResponse>(text, Json); }
        catch (JsonException) { return new ImageGenHealth(false, "unrecognised response", ImageHttp.Truncate(text)); }
        if (status is null) return new ImageGenHealth(false, "unrecognised response", ImageHttp.Truncate(text));

        if (status.Loaded) return new ImageGenHealth(true, "ready", status.ModelName);
        var model = ParseModel(settings.ImageModel);
        return model is null
            ? new ImageGenHealth(false, "no image model loaded",
                "Load one on Unsloth Studio's Images page, or set the image model to load.")
            : new ImageGenHealth(true, "idle", $"{settings.ImageModel!.Trim()} will be loaded before the first image");
    }

    /// <summary>Loads <see cref="IImageGenSettings.ImageModel"/> when Studio has no image model on the GPU.</summary>
    private async Task EnsureModelLoadedAsync(IImageGenSettings settings, CancellationToken ct)
    {
        using var http = CreateClient(settings, TimeSpan.FromSeconds(60));
        var status = await GetStatusAsync(http, ct);
        if (status.Loaded) return;

        var model = ParseModel(settings.ImageModel) ?? throw new InvalidOperationException(
            "Unsloth Studio has no image model loaded. Load one on its Images page, or set the image model to load.");

        using (var content = ImageHttp.JsonContent(model, Json))
        using (var response = await http.PostAsync("api/inference/images/load", content, ct))
        {
            await ImageHttp.EnsureSuccessAsync(response, Server, "images/load", ct);
            var started = await response.Content.ReadFromJsonAsync<StatusResponse>(Json, ct);
            if (started?.Loaded == true) return;
        }

        // The load runs in the background on Studio's side; poll until it lands, fails or runs out of time.
        var deadline = DateTime.UtcNow + LoadTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(LoadPollInterval, ct);
            if ((await GetStatusAsync(http, ct)).Loaded) return;
            using var progress = await http.GetAsync("api/inference/images/load-progress", ct);
            if (!progress.IsSuccessStatusCode) continue;
            var p = await progress.Content.ReadFromJsonAsync<LoadProgressResponse>(Json, ct);
            if (p?.Phase == "error")
                throw new InvalidOperationException($"Unsloth Studio could not load {settings.ImageModel}: {p.Error ?? "unknown error"}");
        }
        throw new TimeoutException(
            $"Unsloth Studio did not finish loading {settings.ImageModel} within {LoadTimeout.TotalMinutes:0} minutes.");
    }

    private static async Task<StatusResponse> GetStatusAsync(HttpClient http, CancellationToken ct)
    {
        using var response = await http.GetAsync("api/inference/images/status", ct);
        await ImageHttp.EnsureSuccessAsync(response, Server, "images/status", ct);
        return await response.Content.ReadFromJsonAsync<StatusResponse>(Json, ct)
            ?? throw new InvalidOperationException("Unsloth Studio returned an empty /images/status response.");
    }

    /// <summary>
    /// Unset steps and CFG are left out, so Studio applies its own defaults (9 steps, guidance 0 — Z-Image Turbo's
    /// recipe) instead of the Z-Image server's 8 / 1.0. Other Studio models need their steps and CFG set.
    /// </summary>
    internal static GenerateRequest BuildRequest(ImageGenRequest request, IImageGenSettings settings) => new(
        Prompt: request.Prompt,
        NegativePrompt: string.IsNullOrWhiteSpace(request.NegativePrompt) ? null : request.NegativePrompt,
        Width: request.Width,
        Height: request.Height,
        Steps: settings.ImageSteps is > 0 ? settings.ImageSteps : null,
        Guidance: settings.ImageCfg is > 0 ? settings.ImageCfg : null,
        Seed: request.Seed & MaxSeed);

    /// <summary>
    /// <c>owner/repo</c> or <c>owner/repo:file.gguf</c> (also <c>.safetensors</c>). Only a trailing checkpoint
    /// filename is split off, so a Windows path such as <c>C:\models\z-image</c> stays whole.
    /// </summary>
    internal static LoadRequest? ParseModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        model = model.Trim();
        var colon = model.LastIndexOf(':');
        if (colon > 0 && colon < model.Length - 1)
        {
            var file = model[(colon + 1)..].Trim();
            if (file.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
                return new LoadRequest(model[..colon].Trim(), file);
        }
        return new LoadRequest(model, null);
    }

    /// <summary>
    /// Studio's routes live at the server root; accept the base URL with or without the <c>/v1</c> suffix the
    /// text-model configuration uses, so the same address can be pasted into both.
    /// </summary>
    internal static string BaseEndpoint(string endpoint)
    {
        var e = endpoint.Trim().TrimEnd('/');
        foreach (var suffix in new[] { "/v1", "/api/inference", "/api" })
            if (e.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return e[..^suffix.Length];
        return e;
    }

    private HttpClient CreateClient(IImageGenSettings settings, TimeSpan? timeout = null) =>
        ImageHttp.CreateClient(_clientFactory, BaseEndpoint(ImageHttp.RequireEndpoint(settings)), settings, timeout);

    internal sealed record GenerateRequest(
        string Prompt, string? NegativePrompt, int Width, int Height, int? Steps, double? Guidance, long Seed);

    internal sealed record LoadRequest(string ModelPath, string? GgufFilename);

    private sealed record GenerateResponse(List<GalleryImage>? Images);

    private sealed record GalleryImage(string? Url, long? Seed);

    private sealed record StatusResponse(bool Loaded, string? RepoId, string? DisplayRepoId, string? GgufFilename)
    {
        public string? ModelName => (DisplayRepoId ?? RepoId) is { } repo
            ? GgufFilename is null ? repo : $"{repo}:{GgufFilename}"
            : null;
    }

    private sealed record LoadProgressResponse(string? Phase, string? Error);
}
