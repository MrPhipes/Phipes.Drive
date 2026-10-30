using SkiaSharp;

namespace Phipes.Drive.Core.Photos;

public interface IThumbnailGenerator
{
    /// <summary>
    /// Writes every configured thumbnail size for one media file. Returns false when the file
    /// cannot be decoded (unsupported codec, or a video with no ffmpeg available) instead of
    /// throwing, so one odd file never breaks a scan.
    /// </summary>
    Task<bool> GenerateAsync(
        string hash,
        MediaKind kind,
        Func<CancellationToken, Task<Stream>> openRead,
        string? physicalPath,
        CancellationToken ct = default);
}

/// <summary>
/// SkiaSharp-based thumbnailer. EXIF orientation is applied while resizing, and ffmpeg (when
/// installed) covers video poster frames plus image formats Skia cannot decode, such as HEIC.
/// </summary>
public sealed class ThumbnailGenerator(PhotoOptions options, Ffmpeg ffmpeg, string thumbsRoot)
    : IThumbnailGenerator
{
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    public string ThumbsRoot { get; } = Path.GetFullPath(thumbsRoot);

    public async Task<bool> GenerateAsync(
        string hash,
        MediaKind kind,
        Func<CancellationToken, Task<Stream>> openRead,
        string? physicalPath,
        CancellationToken ct = default)
    {
        if (kind == MediaKind.None) return false;

        if (kind == MediaKind.Image)
        {
            await using var stream = await openRead(ct).ConfigureAwait(false);
            if (await TryWriteAllSizesAsync(stream, hash, ct).ConfigureAwait(false)) return true;
            // HEIC and friends: let ffmpeg decode a frame if it is installed.
        }

        if (physicalPath is null || !ffmpeg.IsAvailable) return false;

        var temp = Path.Combine(Path.GetTempPath(), $"phipes-thumb-{hash[..16]}-{Guid.NewGuid():N}.png");
        try
        {
            var decoded = kind == MediaKind.Video
                ? await ffmpeg.TryExtractFrameAsync(physicalPath, temp, ct).ConfigureAwait(false)
                : await ffmpeg.TryDecodeImageAsync(physicalPath, temp, ct).ConfigureAwait(false);
            if (!decoded || !File.Exists(temp)) return false;

            await using var frame = File.OpenRead(temp);
            return await TryWriteAllSizesAsync(frame, hash, ct).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private async Task<bool> TryWriteAllSizesAsync(Stream stream, string hash, CancellationToken ct)
    {
        using var codec = SKCodec.Create(stream);
        if (codec is null) return false;

        var origin = codec.EncodedOrigin;
        var swaps = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var (width, height) = swaps
            ? (codec.Info.Height, codec.Info.Width)
            : (codec.Info.Width, codec.Info.Height);

        using var source = Decode(codec, ThumbnailPaths.Sizes.Max());
        if (source is null) return false;
        var (orientedWidth, orientedHeight) = swaps
            ? (source.Height, source.Width)
            : (source.Width, source.Height);

        var quality = Math.Clamp(options.JpegQuality, 1, 100);
        foreach (var size in ThumbnailPaths.Sizes)
        {
            ct.ThrowIfCancellationRequested();
            var target = ThumbnailPaths.For(ThumbsRoot, hash, size);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            // Never upscale: a 400 px original keeps its size in the 1024 bucket.
            var ratio = Math.Min(1.0, (double)size / Math.Max(width, height));
            var info = new SKImageInfo(
                Math.Max(1, (int)Math.Round(width * ratio)),
                Math.Max(1, (int)Math.Round(height * ratio)),
                SKColorType.Rgba8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(info);
            if (surface is null) return false;
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.White); // JPEG has no alpha, so transparent PNGs land on white
            canvas.SetMatrix(SKMatrix.CreateScale(
                    (float)info.Width / orientedWidth, (float)info.Height / orientedHeight)
                .PreConcat(OrientationMatrix(origin, source.Width, source.Height)));
            canvas.DrawImage(source, 0, 0, Sampling);

            using var snapshot = surface.Snapshot();
            using var jpeg = snapshot.Encode(SKEncodedImageFormat.Jpeg, quality);
            if (jpeg is null) return false;

            var temp = target + ".tmp";
            await using (var output = File.Create(temp))
            {
                await output.WriteAsync(jpeg.ToArray(), ct).ConfigureAwait(false);
            }
            File.Move(temp, target, overwrite: true);
        }

        return true;
    }

    /// <summary>
    /// Decodes at the smallest scale the codec offers natively (JPEG: 1/8 steps) that still covers
    /// the largest thumbnail, so a 12 MP photo is never decoded at full resolution.
    /// </summary>
    private static SKImage? Decode(SKCodec codec, int longestTarget)
    {
        var dimensions = codec.Info.Size;
        foreach (var scale in new[] { 0.125f, 0.25f, 0.5f })
        {
            var scaled = codec.GetScaledDimensions(scale);
            if (Math.Max(scaled.Width, scaled.Height) >= longestTarget)
            {
                dimensions = scaled;
                break;
            }
        }

        var info = new SKImageInfo(dimensions.Width, dimensions.Height, SKColorType.Rgba8888,
            codec.Info.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;

        return SKImage.FromPixelCopy(bitmap.PeekPixels());
    }

    /// <summary>Maps decoded pixel coordinates onto the upright frame for an EXIF orientation.</summary>
    private static SKMatrix OrientationMatrix(SKEncodedOrigin origin, int width, int height) => origin switch
    {
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, width, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, width, 0, -1, height, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, height, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, height, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, height, -1, 0, width, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, width, 0, 0, 1),
        _ => SKMatrix.Identity,
    };

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
