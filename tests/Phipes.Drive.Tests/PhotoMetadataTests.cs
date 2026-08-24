using Phipes.Drive.Core.Photos;

namespace Phipes.Drive.Tests;

public sealed class ExifDatesTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Theory]
    [InlineData("2026:08:24 14:03:11")]
    [InlineData("2026-08-24 14:03:11")]
    [InlineData("2026:08:24 14:03:11\0")]
    public void Exif_wall_clock_shapes_are_parsed(string raw)
    {
        Assert.True(ExifDates.TryParseWallClock(raw, out var parsed));
        Assert.Equal(new DateTime(2026, 8, 24, 14, 3, 11), parsed);
        Assert.Equal(DateTimeKind.Unspecified, parsed.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0000:00:00 00:00:00")]
    [InlineData("no es una fecha")]
    public void Unusable_exif_dates_are_rejected(string? raw) =>
        Assert.False(ExifDates.TryParseWallClock(raw, out _));

    [Theory]
    [InlineData("+02:00", 2)]
    [InlineData("-04:00", -4)]
    [InlineData("Z", 0)]
    public void Exif_offsets_are_parsed(string raw, int expectedHours)
    {
        Assert.True(ExifDates.TryParseOffset(raw, out var offset));
        Assert.Equal(TimeSpan.FromHours(expectedHours), offset);
    }

    [Fact]
    public void Exif_date_wins_over_the_file_timestamp()
    {
        var fileModified = new DateTimeOffset(2026, 8, 24, 20, 0, 0, TimeSpan.Zero);
        var exif = new DateTime(2019, 3, 7, 9, 15, 0);

        var (taken, fromExif) = ExifDates.Resolve(exif, null, fileModified, Utc);

        Assert.True(fromExif);
        Assert.Equal(new DateTimeOffset(2019, 3, 7, 9, 15, 0, TimeSpan.Zero), taken);
    }

    [Fact]
    public void An_explicit_exif_offset_is_converted_to_utc()
    {
        var fileModified = new DateTimeOffset(2026, 8, 24, 20, 0, 0, TimeSpan.Zero);
        var exif = new DateTime(2026, 8, 24, 14, 0, 0);

        var (taken, fromExif) = ExifDates.Resolve(exif, TimeSpan.FromHours(-4), fileModified, Utc);

        Assert.True(fromExif);
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 18, 0, 0, TimeSpan.Zero), taken);
    }

    [Fact]
    public void Missing_exif_falls_back_to_the_file_timestamp()
    {
        var fileModified = new DateTimeOffset(2026, 8, 24, 20, 0, 0, TimeSpan.Zero);

        var (taken, fromExif) = ExifDates.Resolve(null, null, fileModified, Utc);

        Assert.False(fromExif);
        Assert.Equal(fileModified, taken);
    }

    [Fact]
    public void An_implausible_exif_date_falls_back_to_the_file_timestamp()
    {
        var fileModified = new DateTimeOffset(2026, 8, 24, 20, 0, 0, TimeSpan.Zero);

        var (fromPast, pastUsedExif) =
            ExifDates.Resolve(new DateTime(1899, 12, 31, 0, 0, 0), null, fileModified, Utc);
        var (fromFuture, futureUsedExif) = ExifDates.Resolve(
            new DateTime(2200, 1, 1, 0, 0, 0), null, fileModified, Utc, now: fileModified);

        Assert.False(pastUsedExif);
        Assert.Equal(fileModified, fromPast);
        Assert.False(futureUsedExif);
        Assert.Equal(fileModified, fromFuture);
    }
}

public sealed class MediaTypesTests
{
    [Theory]
    [InlineData("foto.jpg", MediaKind.Image)]
    [InlineData("FOTO.JPEG", MediaKind.Image)]
    [InlineData("captura.PNG", MediaKind.Image)]
    [InlineData("iphone.heic", MediaKind.Image)]
    [InlineData("meme.webp", MediaKind.Image)]
    [InlineData("animado.gif", MediaKind.Image)]
    [InlineData("clip.mp4", MediaKind.Video)]
    [InlineData("clip.MOV", MediaKind.Video)]
    [InlineData("antiguo.3gp", MediaKind.Video)]
    [InlineData("pelicula.mkv", MediaKind.Video)]
    [InlineData("informe.pdf", MediaKind.None)]
    [InlineData("sin-extension", MediaKind.None)]
    [InlineData("foto.jpg.uploading", MediaKind.None)]
    public void Extensions_are_classified(string fileName, MediaKind expected) =>
        Assert.Equal(expected, MediaTypes.Classify(fileName));
}
