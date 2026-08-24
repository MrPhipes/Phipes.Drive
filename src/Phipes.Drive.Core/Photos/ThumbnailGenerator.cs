using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

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
/// ImageSharp-based thumbnailer. EXIF orientation is applied before resizing, and ffmpeg (when
/// installed) covers video poster frames plus image formats ImageSharp cannot decode, such as HEIC.
/// </summary>
public sealed class ThumbnailGenerator(PhotoOptions options, Ffmpeg ffmpeg, string thumbsRoot)
    : IThumbnailGenerator
{
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
            try
            {
                await using var stream = await openRead(ct).ConfigureAwait(false);
                using var image = await Image.LoadAsync(stream, ct).ConfigureAwait(false);
                return await WriteAllSizesAsync(image, hash, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is UnknownImageFormatException or InvalidImageContentException
                                          or NotSupportedException)
            {
                // HEIC and friends: let ffmpeg decode a frame if it is installed.
            }
        }

        if (physicalPath is null || !ffmpeg.IsAvailable) return false;

        var temp = Path.Combine(Path.GetTempPath(), $"phipes-thumb-{hash[..16]}-{Guid.NewGuid():N}.png");
        try
        {
            var decoded = kind == MediaKind.Video
                ? await ffmpeg.TryExtractFrameAsync(physicalPath, temp, ct).ConfigureAwait(false)
                : await ffmpeg.TryDecodeImageAsync(physicalPath, temp, ct).ConfigureAwait(false);
            if (!decoded || !File.Exists(temp)) return false;

            using var image = await Image.LoadAsync(temp, ct).ConfigureAwait(false);
            return await WriteAllSizesAsync(image, hash, ct).ConfigureAwait(false);
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

    private async Task<bool> WriteAllSizesAsync(Image image, string hash, CancellationToken ct)
    {
        image.Mutate(x => x.AutoOrient());

        var encoder = new JpegEncoder { Quality = Math.Clamp(options.JpegQuality, 1, 100) };
        foreach (var size in ThumbnailPaths.Sizes)
        {
            ct.ThrowIfCancellationRequested();
            var target = ThumbnailPaths.For(ThumbsRoot, hash, size);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            // Never upscale: a 400 px original keeps its size in the 1024 bucket.
            var longestSide = Math.Max(image.Width, image.Height);
            using var resized = longestSide <= size
                ? image.Clone(_ => { })
                : image.Clone(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(size, size),
                    Mode = ResizeMode.Max,
                }));

            var temp = target + ".tmp";
            await resized.SaveAsJpegAsync(temp, encoder, ct).ConfigureAwait(false);
            File.Move(temp, target, overwrite: true);
        }

        return true;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
