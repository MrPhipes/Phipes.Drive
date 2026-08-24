using Phipes.Drive.Core.Photos;

namespace Phipes.Drive.Tests;

public sealed class ThumbnailPathsTests
{
    private const string Hash = "9f2b1c8ad4e6035712aa4bd0c9e1f3a58d7602b4c1e9f8a3d5b60c7e2149af83";

    [Fact]
    public void Thumbnails_default_to_a_sibling_of_the_storage_root()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), "phipes", "storage");

        var thumbsRoot = ThumbnailPaths.ResolveRoot(storageRoot, null);

        Assert.Equal(Path.Combine(Path.GetTempPath(), "phipes", "thumbs"), thumbsRoot);
        // Critical: never inside the tree the user browses.
        Assert.False(thumbsRoot.StartsWith(storageRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    [Fact]
    public void A_configured_thumbs_path_wins()
    {
        var configured = Path.Combine(Path.GetTempPath(), "otro-disco", "thumbs");

        Assert.Equal(Path.GetFullPath(configured),
            ThumbnailPaths.ResolveRoot("/data/storage", configured));
    }

    [Fact]
    public void Thumbnails_are_sharded_by_the_first_two_hash_characters()
    {
        var root = Path.Combine(Path.GetTempPath(), "phipes", "thumbs");

        var small = ThumbnailPaths.For(root, Hash, 256);
        var large = ThumbnailPaths.For(root, Hash, 1024);

        Assert.Equal(Path.Combine(root, "9f", $"{Hash}_256.jpg"), small);
        Assert.Equal(Path.Combine(root, "9f", $"{Hash}_1024.jpg"), large);
    }

    [Theory]
    [InlineData(256, true)]
    [InlineData(1024, true)]
    [InlineData(512, false)]
    [InlineData(0, false)]
    public void Only_the_two_published_sizes_are_supported(int size, bool expected) =>
        Assert.Equal(expected, ThumbnailPaths.IsSupportedSize(size));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../../etc/passwd")]
    [InlineData("9F2B1C8AD4E6035712AA4BD0C9E1F3A58D7602B4C1E9F8A3D5B60C7E2149AF83")]
    [InlineData("9f2b1c8a")]
    public void Ids_that_are_not_a_lowercase_sha256_are_refused(string? hash)
    {
        Assert.False(ThumbnailPaths.IsValidHash(hash));
        if (hash is not null)
            Assert.Throws<ArgumentException>(() => ThumbnailPaths.For("/thumbs", hash, 256));
    }

    [Fact]
    public void An_unsupported_size_is_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ThumbnailPaths.For("/thumbs", Hash, 512));
}
