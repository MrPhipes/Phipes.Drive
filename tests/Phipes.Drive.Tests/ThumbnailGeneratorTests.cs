using Phipes.Drive.Core.Photos;
using SkiaSharp;

namespace Phipes.Drive.Tests;

public sealed class ThumbnailGeneratorTests : IDisposable
{
    private const string Hash = "1122334455667788990011223344556677889900112233445566778899001122";

    private readonly string _work;
    private readonly string _thumbsRoot;
    private readonly ThumbnailGenerator _generator;

    public ThumbnailGeneratorTests()
    {
        _work = Path.Combine(Path.GetTempPath(), "phipes-thumb-tests", Guid.NewGuid().ToString("N"));
        _thumbsRoot = Path.Combine(_work, "thumbs");
        Directory.CreateDirectory(_work);
        var options = new PhotoOptions();
        _generator = new ThumbnailGenerator(options, new Ffmpeg(options), _thumbsRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    private static MemoryStream PngOf(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return new MemoryStream(data.ToArray());
    }

    /// <summary>
    /// A JPEG whose left half is red and right half blue, tagged with EXIF orientation 6
    /// ("rotate 90 degrees clockwise to display"). Skia cannot write EXIF, so the APP1 segment
    /// is spliced in right after the SOI marker.
    /// </summary>
    private static MemoryStream RotatedJpegOf(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Blue);
            using var red = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(0, 0, width / 2f, height, red);
        }
        var jpeg = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95).ToArray();

        byte[] app1 =
        [
            0xFF, 0xE1, 0x00, 0x22,                         // APP1, length 34
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'I', (byte)'I', 0x2A, 0x00, 8, 0, 0, 0,   // little-endian TIFF, IFD0 at 8
            1, 0,                                           // one entry
            0x12, 0x01, 3, 0, 1, 0, 0, 0, 6, 0, 0, 0,       // Orientation (SHORT) = 6
            0, 0, 0, 0,                                     // no next IFD
        ];
        return new MemoryStream([.. jpeg[..2], .. app1, .. jpeg[2..]]);
    }

    private static SKSizeI Identify(string path)
    {
        using var codec = SKCodec.Create(path);
        return codec.Info.Size;
    }

    private Task<bool> GenerateAsync(Func<MemoryStream> source) =>
        _generator.GenerateAsync(Hash, MediaKind.Image,
            _ => Task.FromResult<Stream>(source()), physicalPath: null);

    [Fact]
    public async Task Both_sizes_are_written_and_the_longest_side_is_capped()
    {
        Assert.True(await GenerateAsync(() => PngOf(2000, 1000)));

        foreach (var size in ThumbnailPaths.Sizes)
        {
            var path = ThumbnailPaths.For(_thumbsRoot, Hash, size);
            Assert.True(File.Exists(path), $"missing thumbnail for size {size}");

            var info = Identify(path);
            Assert.Equal(size, info.Width);
            Assert.Equal(size / 2, info.Height); // aspect ratio preserved
        }
    }

    [Fact]
    public async Task Small_originals_are_never_upscaled()
    {
        Assert.True(await GenerateAsync(() => PngOf(120, 80)));

        var info = Identify(ThumbnailPaths.For(_thumbsRoot, Hash, 1024));
        Assert.Equal(120, info.Width);
        Assert.Equal(80, info.Height);
    }

    [Fact]
    public async Task Exif_orientation_is_applied()
    {
        Assert.True(await GenerateAsync(() => RotatedJpegOf(200, 100)));

        var path = ThumbnailPaths.For(_thumbsRoot, Hash, 1024);
        Assert.Equal(new SKSizeI(100, 200), Identify(path));

        // Rotating clockwise moves the red left half to the top.
        using var thumb = SKBitmap.Decode(path);
        var top = thumb.GetPixel(50, 20);
        var bottom = thumb.GetPixel(50, 180);
        Assert.True(top.Red > 200 && top.Blue < 60, $"top should be red, was {top}");
        Assert.True(bottom.Blue > 200 && bottom.Red < 60, $"bottom should be blue, was {bottom}");
    }

    [Fact]
    public async Task Large_jpegs_are_scaled_down_exactly()
    {
        Assert.True(await GenerateAsync(() => RotatedJpegOf(4000, 3000)));

        // Orientation 6 makes the 4000x3000 frame portrait; native 1/2 decode feeds the resize.
        Assert.Equal(new SKSizeI(768, 1024), Identify(ThumbnailPaths.For(_thumbsRoot, Hash, 1024)));
        Assert.Equal(new SKSizeI(192, 256), Identify(ThumbnailPaths.For(_thumbsRoot, Hash, 256)));
    }

    [Fact]
    public async Task An_undecodable_file_reports_failure_instead_of_throwing()
    {
        var generated = await _generator.GenerateAsync(Hash, MediaKind.Image,
            _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3, 4, 5])), physicalPath: null);

        Assert.False(generated);
        Assert.False(File.Exists(ThumbnailPaths.For(_thumbsRoot, Hash, 256)));
    }

    [Fact]
    public async Task A_video_without_a_physical_path_reports_failure()
    {
        var generated = await _generator.GenerateAsync(Hash, MediaKind.Video,
            _ => Task.FromResult<Stream>(new MemoryStream([0])), physicalPath: null);

        Assert.False(generated);
    }
}
