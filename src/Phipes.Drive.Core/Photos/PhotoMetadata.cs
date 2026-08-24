namespace Phipes.Drive.Core.Photos;

/// <summary>Everything the indexer needs to know about a media file beyond its bytes.</summary>
public sealed record PhotoMetadata(
    DateTimeOffset TakenUtc,
    bool TakenFromExif,
    int? Width,
    int? Height,
    double? Latitude,
    double? Longitude,
    long? DurationMs);

public interface IPhotoMetadataReader
{
    /// <summary>
    /// Reads capture date, dimensions, GPS and (for video) duration. Never throws for unreadable
    /// files: it degrades to the file modification time with no dimensions.
    /// </summary>
    Task<PhotoMetadata> ReadAsync(
        Func<CancellationToken, Task<Stream>> openRead,
        string fileName,
        MediaKind kind,
        DateTimeOffset fileModifiedUtc,
        string? physicalPath,
        CancellationToken ct = default);
}
