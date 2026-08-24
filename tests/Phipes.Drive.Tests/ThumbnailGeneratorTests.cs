using Phipes.Drive.Core.Photos;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

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
        using var image = new Image<Rgba32>(width, height);
        var stream = new MemoryStream();
        image.SaveAsPng(stream);
        stream.Position = 0;
        return stream;
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

            var info = await Image.IdentifyAsync(path);
            Assert.Equal(size, info.Width);
            Assert.Equal(size / 2, info.Height); // aspect ratio preserved
        }
    }

    [Fact]
    public async Task Small_originals_are_never_upscaled()
    {
        Assert.True(await GenerateAsync(() => PngOf(120, 80)));

        var info = await Image.IdentifyAsync(ThumbnailPaths.For(_thumbsRoot, Hash, 1024));
        Assert.Equal(120, info.Width);
        Assert.Equal(80, info.Height);
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
