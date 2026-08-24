namespace Phipes.Drive.Core.Photos;

/// <summary>
/// Resolves where a thumbnail lives on disk. Kept free of I/O so it can be unit tested, and
/// strict about the hash format because the hash arrives from the public API as a route value.
/// </summary>
public static class ThumbnailPaths
{
    /// <summary>The only thumbnail widths the pipeline produces (longest side, in pixels).</summary>
    public static readonly int[] Sizes = [256, 1024];

    public const int SmallSize = 256;
    public const int LargeSize = 1024;

    public static bool IsSupportedSize(int size) => Array.IndexOf(Sizes, size) >= 0;

    /// <summary>True for a 64-character lowercase hex string, the only shape we accept as an id.</summary>
    public static bool IsValidHash(string? hash)
    {
        if (hash is null || hash.Length != 64) return false;
        foreach (var c in hash)
        {
            var ok = c is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>
    /// Absolute thumbnail root: the configured path when set, otherwise a "thumbs" folder next to
    /// (not inside) the storage root.
    /// </summary>
    public static string ResolveRoot(string storageRootPath, string? configuredThumbsPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredThumbsPath))
            return Path.GetFullPath(configuredThumbsPath);

        var storageRoot = Path.GetFullPath(storageRootPath);
        return Path.GetFullPath(Path.Combine(storageRoot, "..", "thumbs"));
    }

    /// <summary>Absolute path of one thumbnail: <c>&lt;root&gt;/&lt;ab&gt;/&lt;hash&gt;_&lt;size&gt;.jpg</c>.</summary>
    public static string For(string thumbsRoot, string hash, int size)
    {
        if (!IsValidHash(hash))
            throw new ArgumentException("Hash must be 64 lowercase hex characters.", nameof(hash));
        if (!IsSupportedSize(size))
            throw new ArgumentOutOfRangeException(nameof(size), size, "Unsupported thumbnail size.");

        return Path.Combine(Path.GetFullPath(thumbsRoot), hash[..2], $"{hash}_{size}.jpg");
    }
}
