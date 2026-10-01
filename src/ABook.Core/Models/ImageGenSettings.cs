namespace ABook.Core.Models;

/// <summary>
/// Image-generation settings carried by both <see cref="LlmConfiguration"/> and <see cref="LlmPreset"/>,
/// so applying a preset switches the text model and the image model together. Null fields fall back to
/// <see cref="ImageGenDefaults"/>. Image generation is configured when <see cref="ImageEndpoint"/> is set.
/// </summary>
public interface IImageGenSettings
{
    ImageProvider? ImageProvider { get; }
    string? ImageEndpoint { get; }
    int? ImageSteps { get; }
    double? ImageCfg { get; }
    int? ImagePortraitWidth { get; }
    int? ImagePortraitHeight { get; }
    int? ImageLandscapeWidth { get; }
    int? ImageLandscapeHeight { get; }
    int? ImageTimeoutMs { get; }
    /// <summary>Free-text art direction handed to the illustration planner, which writes it into every prompt.</summary>
    string? ImageVisualStyle { get; }
    /// <summary>
    /// How many illustrations the planner aims for in a chapter. It may plan more when a chapter genuinely
    /// needs them, or fewer when there is little worth illustrating.
    /// </summary>
    int? ImagesPerChapter { get; }
    /// <summary>Ask Ollama to unload the text model before a render run, freeing VRAM for the image model.</summary>
    bool UnloadLlmBeforeRender { get; }
}

/// <summary>The stored form of <see cref="IImageGenSettings"/> (configuration and preset rows).</summary>
public interface IEditableImageGenSettings : IImageGenSettings
{
    new ImageProvider? ImageProvider { get; set; }
    new string? ImageEndpoint { get; set; }
    new int? ImageSteps { get; set; }
    new double? ImageCfg { get; set; }
    new int? ImagePortraitWidth { get; set; }
    new int? ImagePortraitHeight { get; set; }
    new int? ImageLandscapeWidth { get; set; }
    new int? ImageLandscapeHeight { get; set; }
    new int? ImageTimeoutMs { get; set; }
    new string? ImageVisualStyle { get; set; }
    new int? ImagesPerChapter { get; set; }
    new bool UnloadLlmBeforeRender { get; set; }
}

/// <summary>Defaults matching Z-Image Turbo (distilled for 8 steps at cfg 1.0).</summary>
public static class ImageGenDefaults
{
    public const int Steps = 8;
    public const double Cfg = 1.0;
    public const int PortraitWidth = 768;
    public const int PortraitHeight = 1152;
    public const int LandscapeWidth = 1152;
    public const int LandscapeHeight = 768;
    public const int TimeoutMs = 300_000;
    public const int ImagesPerChapter = 2;
}

public static class ImageGenSettingsExtensions
{
    public static bool HasImageGeneration(this IImageGenSettings s) => !string.IsNullOrWhiteSpace(s.ImageEndpoint);

    /// <summary>The configured base number of illustrations per chapter, or the default.</summary>
    public static int ImagesPerChapterOrDefault(this IImageGenSettings? s) =>
        s?.ImagesPerChapter is > 0 ? s.ImagesPerChapter.Value : ImageGenDefaults.ImagesPerChapter;

    public static void CopyImageSettingsFrom(this IEditableImageGenSettings target, IImageGenSettings source)
    {
        target.ImageProvider = source.ImageProvider;
        target.ImageEndpoint = string.IsNullOrWhiteSpace(source.ImageEndpoint) ? null : source.ImageEndpoint.Trim();
        target.ImageSteps = source.ImageSteps;
        target.ImageCfg = source.ImageCfg;
        target.ImagePortraitWidth = source.ImagePortraitWidth;
        target.ImagePortraitHeight = source.ImagePortraitHeight;
        target.ImageLandscapeWidth = source.ImageLandscapeWidth;
        target.ImageLandscapeHeight = source.ImageLandscapeHeight;
        target.ImageTimeoutMs = source.ImageTimeoutMs;
        target.ImageVisualStyle = source.ImageVisualStyle;
        target.ImagesPerChapter = source.ImagesPerChapter is > 0 ? source.ImagesPerChapter : null;
        target.UnloadLlmBeforeRender = source.UnloadLlmBeforeRender;
    }

    /// <summary>Pixel size for an orientation. Square uses the area of the portrait size, rounded to a multiple of 64.</summary>
    public static (int Width, int Height) ImageSize(this IImageGenSettings s, IllustrationOrientation orientation)
    {
        var pw = s.ImagePortraitWidth is > 0 ? s.ImagePortraitWidth.Value : ImageGenDefaults.PortraitWidth;
        var ph = s.ImagePortraitHeight is > 0 ? s.ImagePortraitHeight.Value : ImageGenDefaults.PortraitHeight;
        return orientation switch
        {
            IllustrationOrientation.Landscape => (
                s.ImageLandscapeWidth is > 0 ? s.ImageLandscapeWidth.Value : ImageGenDefaults.LandscapeWidth,
                s.ImageLandscapeHeight is > 0 ? s.ImageLandscapeHeight.Value : ImageGenDefaults.LandscapeHeight),
            IllustrationOrientation.Square => Square(pw, ph),
            _ => (pw, ph),
        };

        static (int, int) Square(int w, int h)
        {
            var side = Math.Max(64, (int)Math.Round(Math.Sqrt((double)w * h) / 64) * 64);
            return (side, side);
        }
    }
}
