using System.Globalization;

namespace Phipes.Drive.Core.Photos;

/// <summary>
/// Capture-date logic, isolated from any I/O so it can be unit tested. EXIF stores a bare wall
/// clock with no time zone, so an assumed zone (or the optional OffsetTime tag) turns it into an
/// instant; when EXIF is missing or implausible the file's modification time is used instead.
/// </summary>
public static class ExifDates
{
    private static readonly string[] WallClockFormats =
    [
        "yyyy:MM:dd HH:mm:ss",
        "yyyy:MM:dd HH:mm",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy:MM:dd",
        "yyyy-MM-dd",
    ];

    /// <summary>Earliest date we accept from EXIF; anything older is treated as a broken tag.</summary>
    public static readonly DateTime MinPlausible = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    /// <summary>Parses the EXIF date shape "2026:08:24 14:03:11" (and a few common variants).</summary>
    public static bool TryParseWallClock(string? raw, out DateTime result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var value = raw.Trim().TrimEnd('\0').Trim();
        // Cameras that never had their clock set write all-zero tags.
        if (value.StartsWith("0000", StringComparison.Ordinal)) return false;

        if (!DateTime.TryParseExact(value, WallClockFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
            return false;

        result = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        return true;
    }

    /// <summary>Parses the EXIF OffsetTime shape "+02:00" / "-04:00" / "Z".</summary>
    public static bool TryParseOffset(string? raw, out TimeSpan offset)
    {
        offset = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var value = raw.Trim().TrimEnd('\0').Trim();
        if (value is "Z" or "z") return true;

        var sign = value[0] switch { '+' => 1, '-' => -1, _ => 0 };
        if (sign == 0) return false;

        if (!TimeSpan.TryParseExact(value[1..], @"hh\:mm", CultureInfo.InvariantCulture, out var magnitude))
            return false;

        offset = sign > 0 ? magnitude : -magnitude;
        return true;
    }

    /// <summary>
    /// Picks the capture instant: the EXIF wall clock when it is present and plausible, the file
    /// modification time otherwise. The boolean says which source won.
    /// </summary>
    public static (DateTimeOffset TakenUtc, bool FromExif) Resolve(
        DateTime? exifWallClock,
        TimeSpan? exifOffset,
        DateTimeOffset fileModifiedUtc,
        TimeZoneInfo? assumedZone = null,
        DateTimeOffset? now = null)
    {
        var upperBound = (now ?? DateTimeOffset.UtcNow).AddDays(1);

        if (exifWallClock is { } wall && wall >= MinPlausible)
        {
            var offset = exifOffset ?? (assumedZone ?? TimeZoneInfo.Local).GetUtcOffset(wall);
            var taken = new DateTimeOffset(wall, offset).ToUniversalTime();
            if (taken <= upperBound)
                return (taken, true);
        }

        return (fileModifiedUtc.ToUniversalTime(), false);
    }
}
