using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using SixLabors.ImageSharp;
using Directory = MetadataExtractor.Directory;

namespace Phipes.Drive.Core.Photos;

/// <summary>
/// Reads EXIF with MetadataExtractor (managed, no native dependencies), falls back to ImageSharp's
/// header-only identify for dimensions, and to ffprobe for video duration.
/// </summary>
public sealed class ExifPhotoMetadataReader(Ffmpeg ffmpeg, TimeZoneInfo? assumedZone = null) : IPhotoMetadataReader
{
    private const int TagOffsetTime = 0x9010;
    private const int TagOffsetTimeOriginal = 0x9011;

    private readonly TimeZoneInfo _assumedZone = assumedZone ?? TimeZoneInfo.Local;

    public async Task<PhotoMetadata> ReadAsync(
        Func<CancellationToken, Task<Stream>> openRead,
        string fileName,
        MediaKind kind,
        DateTimeOffset fileModifiedUtc,
        string? physicalPath,
        CancellationToken ct = default)
    {
        DateTime? wallClock = null;
        TimeSpan? offset = null;
        int? width = null, height = null;
        double? latitude = null, longitude = null;
        var orientation = 1;

        try
        {
            await using var stream = await openRead(ct).ConfigureAwait(false);
            var directories = ImageMetadataReader.ReadMetadata(stream);

            wallClock = ReadWallClock(directories);
            offset = ReadOffset(directories);
            (latitude, longitude) = ReadLocation(directories);
            orientation = ReadOrientation(directories);
            (width, height) = ReadDimensions(directories);
        }
        catch
        {
            // Unsupported container or damaged file: keep going with what the filesystem knows.
        }

        if (kind == MediaKind.Image && (width is null or 0 || height is null or 0))
            (width, height) = await IdentifyAsync(openRead, ct).ConfigureAwait(false);

        // EXIF orientations 5-8 rotate by 90 degrees, so the displayed frame is the transpose.
        if (orientation is >= 5 and <= 8) (width, height) = (height, width);

        long? durationMs = null;
        if (kind == MediaKind.Video && physicalPath is not null)
            durationMs = await ffmpeg.TryGetDurationMsAsync(physicalPath, ct).ConfigureAwait(false);

        var (takenUtc, fromExif) = ExifDates.Resolve(wallClock, offset, fileModifiedUtc, _assumedZone);
        return new PhotoMetadata(takenUtc, fromExif, Positive(width), Positive(height),
            latitude, longitude, durationMs);
    }

    private static int? Positive(int? value) => value is > 0 ? value : null;

    private static async Task<(int?, int?)> IdentifyAsync(
        Func<CancellationToken, Task<Stream>> openRead, CancellationToken ct)
    {
        try
        {
            await using var stream = await openRead(ct).ConfigureAwait(false);
            var info = await Image.IdentifyAsync(stream, ct).ConfigureAwait(false);
            return (info.Width, info.Height);
        }
        catch
        {
            return (null, null);
        }
    }

    private static DateTime? ReadWallClock(IReadOnlyList<Directory> directories)
    {
        int[] tags = [ExifDirectoryBase.TagDateTimeOriginal, ExifDirectoryBase.TagDateTimeDigitized,
            ExifDirectoryBase.TagDateTime];

        foreach (var tag in tags)
        foreach (var directory in directories)
        {
            if (!directory.ContainsTag(tag)) continue;
            if (ExifDates.TryParseWallClock(directory.GetString(tag), out var parsed)) return parsed;
        }

        // QuickTime/MP4 creation dates are already real instants; MetadataExtractor exposes them
        // as DateTime values on their own directories.
        foreach (var directory in directories)
        foreach (var tag in directory.Tags)
        {
            if (!tag.Name.Contains("Creation", StringComparison.OrdinalIgnoreCase)) continue;
            if (directory.TryGetDateTime(tag.Type, out var created) && created >= ExifDates.MinPlausible)
                return DateTime.SpecifyKind(created, DateTimeKind.Unspecified);
        }

        return null;
    }

    private static TimeSpan? ReadOffset(IReadOnlyList<Directory> directories)
    {
        // EXIF 2.31 OffsetTime tags; MetadataExtractor has no named constants for them.
        int[] tags = [TagOffsetTimeOriginal, TagOffsetTime];
        foreach (var tag in tags)
        foreach (var directory in directories)
        {
            if (!directory.ContainsTag(tag)) continue;
            if (ExifDates.TryParseOffset(directory.GetString(tag), out var offset)) return offset;
        }
        return null;
    }

    private static (double?, double?) ReadLocation(IReadOnlyList<Directory> directories)
    {
        foreach (var gps in directories.OfType<GpsDirectory>())
        {
            if (gps.GetGeoLocation() is { IsZero: false } location)
                return (location.Latitude, location.Longitude);
        }
        return (null, null);
    }

    private static int ReadOrientation(IReadOnlyList<Directory> directories)
    {
        foreach (var directory in directories)
        {
            if (directory.ContainsTag(ExifDirectoryBase.TagOrientation) &&
                directory.TryGetInt32(ExifDirectoryBase.TagOrientation, out var value) &&
                value is >= 1 and <= 8)
                return value;
        }
        return 1;
    }

    private static (int?, int?) ReadDimensions(IReadOnlyList<Directory> directories)
    {
        int? width = null, height = null;

        foreach (var directory in directories)
        foreach (var tag in directory.Tags)
        {
            if (width is null && IsWidthTag(tag.Name) && directory.TryGetInt32(tag.Type, out var w) && w > 0)
                width = w;
            if (height is null && IsHeightTag(tag.Name) && directory.TryGetInt32(tag.Type, out var h) && h > 0)
                height = h;
        }

        return (width, height);

        static bool IsWidthTag(string name) =>
            name.Equals("Image Width", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Exif Image Width", StringComparison.OrdinalIgnoreCase);

        static bool IsHeightTag(string name) =>
            name.Equals("Image Height", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Exif Image Height", StringComparison.OrdinalIgnoreCase);
    }
}
