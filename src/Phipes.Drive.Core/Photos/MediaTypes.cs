namespace Phipes.Drive.Core.Photos;

public enum MediaKind
{
    None = 0,
    Image = 1,
    Video = 2,
}

/// <summary>Decides, from a file name alone, whether a file belongs in the photo index.</summary>
public static class MediaTypes
{
    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".heic", ".webp", ".gif" };

    private static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".3gp", ".mkv" };

    public static MediaKind Classify(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(extension)) return MediaKind.None;
        if (ImageExtensions.Contains(extension)) return MediaKind.Image;
        if (VideoExtensions.Contains(extension)) return MediaKind.Video;
        return MediaKind.None;
    }

    public static bool IsMedia(string fileName) => Classify(fileName) != MediaKind.None;
}
