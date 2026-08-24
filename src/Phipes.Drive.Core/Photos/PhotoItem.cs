namespace Phipes.Drive.Core.Photos;

/// <summary>
/// One indexed photo or video. The SHA-256 of the file content is the identity: two copies of the
/// same bytes under different paths collapse into a single row keeping the first path found.
/// </summary>
public sealed class PhotoItem
{
    /// <summary>Lowercase hex SHA-256 of the file content. Primary key and public id.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>Storage-relative path ('/' separated) of the copy this row represents.</summary>
    public string Path { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    /// <summary>Last write time of the file when it was indexed; used to detect changes cheaply.</summary>
    public DateTimeOffset ModifiedUtc { get; set; }

    /// <summary>Capture time: EXIF when available, file modification time otherwise.</summary>
    public DateTimeOffset TakenUtc { get; set; }

    /// <summary>True when <see cref="TakenUtc"/> came from EXIF rather than the filesystem.</summary>
    public bool TakenFromExif { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public bool IsVideo { get; set; }

    public long? DurationMs { get; set; }

    /// <summary>True once at least the 256 px thumbnail has been written to the thumbnail store.</summary>
    public bool HasThumb { get; set; }

    /// <summary>True when thumbnail generation was attempted and failed (unsupported codec, no ffmpeg).</summary>
    public bool ThumbFailed { get; set; }

    /// <summary>When this row was created or last refreshed. Drives the <c>since</c> delta query.</summary>
    public DateTimeOffset IndexedUtc { get; set; }
}
