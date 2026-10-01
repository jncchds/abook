using SkiaSharp;

namespace ABook.Api.Services;

/// <summary>
/// Re-encodes illustrations as JPEG for exports. The image model returns lossless PNGs (often over a
/// megabyte each), which are kept as the stored originals; an exported book only needs a
/// reading-quality copy, typically a tenth of the size. JPEG is the one lossy format every target
/// supports — FB2 readers accept only JPEG and PNG.
/// </summary>
internal static class ExportImageCompressor
{
    public const int JpegQuality = 82;
    /// <summary>Longest side of an exported image; larger images are scaled down to it.</summary>
    public const int MaxDimension = 1600;

    /// <summary>
    /// The compressed image, or the original when it cannot be decoded or compressing would not make it
    /// smaller.
    /// </summary>
    public static (byte[] Data, string ContentType) Compress(byte[] data, string contentType)
    {
        try
        {
            using var source = SKBitmap.Decode(data);
            if (source is null || source.Width <= 0 || source.Height <= 0) return (data, contentType);

            var scale = Math.Min(1.0, (double)MaxDimension / Math.Max(source.Width, source.Height));
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));

            // JPEG has no alpha channel: flatten onto white so transparent areas don't turn black.
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            surface.Canvas.Clear(SKColors.White);
            using (var image = SKImage.FromBitmap(source))
                surface.Canvas.DrawImage(image, new SKRect(0, 0, width, height),
                    new SKSamplingOptions(SKCubicResampler.Mitchell));
            using var snapshot = surface.Snapshot();
            using var encoded = snapshot.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
            if (encoded is null) return (data, contentType);

            var bytes = encoded.ToArray();
            return bytes.Length < data.Length ? (bytes, "image/jpeg") : (data, contentType);
        }
        catch (Exception)
        {
            // An image that can't be re-encoded still ships — uncompressed beats missing.
            return (data, contentType);
        }
    }
}
