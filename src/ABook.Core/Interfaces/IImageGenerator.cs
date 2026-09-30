using ABook.Core.Models;

namespace ABook.Core.Interfaces;

public record ImageGenRequest(string Prompt, string NegativePrompt, long Seed, int Width, int Height);

public record GeneratedImage(byte[] Data, string ContentType, long Seed);

public record ImageGenHealth(bool Ready, string Status, string? Detail);

/// <summary>An image model reached over HTTP. Settings come from the book's <see cref="LlmConfiguration"/>.</summary>
public interface IImageGenerator
{
    Task<GeneratedImage> GenerateAsync(ImageGenRequest request, IImageGenSettings settings, CancellationToken ct = default);
    Task<ImageGenHealth> CheckHealthAsync(IImageGenSettings settings, CancellationToken ct = default);
}

public interface IImageGeneratorFactory
{
    IImageGenerator Create(ImageProvider? provider);
}

/// <summary>Frees the text model's VRAM before an image render run. Only providers that support it do anything.</summary>
public interface ILlmModelUnloader
{
    /// <summary>Returns true when the provider was asked to unload the model.</summary>
    Task<bool> UnloadAsync(LlmConfiguration config, CancellationToken ct = default);
}
