namespace Phipes.Drive.App.Services;

/// <summary>A photo or video found on the device.</summary>
/// <param name="Uri">Platform content URI used to open the item.</param>
/// <param name="Name">File name, e.g. IMG_20260807_101500.jpg.</param>
/// <param name="SizeBytes">File size in bytes.</param>
/// <param name="TakenLocal">Capture date (falls back to file modification date).</param>
/// <param name="IsVideo">True for videos, false for images.</param>
public sealed record MediaItem(string Uri, string Name, long SizeBytes, DateTime TakenLocal, bool IsVideo);

/// <summary>Enumerates photos/videos from the device media store.</summary>
public interface IMediaLibrary
{
    /// <summary>Media folders available on the device (relative paths like "DCIM/Camera").</summary>
    Task<IReadOnlyList<string>> GetAvailableFoldersAsync();

    /// <summary>All photos/videos inside the given folders (relative paths, no recursion).</summary>
    Task<IReadOnlyList<MediaItem>> GetItemsAsync(IReadOnlyCollection<string> folders);

    /// <summary>Every photo/video on the device, newest first. Used by the gallery.</summary>
    Task<IReadOnlyList<MediaItem>> GetAllItemsAsync();

    /// <summary>
    /// Square thumbnail as JPEG bytes, straight from the platform thumbnail cache
    /// (no full-size decode). Returns null when the item has no thumbnail.
    /// </summary>
    Task<byte[]?> GetThumbnailAsync(MediaItem item, int size);

    /// <summary>Opens the item content for reading. The stream may not be seekable.</summary>
    Task<Stream> OpenReadAsync(MediaItem item);
}

/// <summary>Placeholder for platforms without a media library implementation yet (iOS pending).</summary>
public sealed class NullMediaLibrary : IMediaLibrary
{
    public Task<IReadOnlyList<string>> GetAvailableFoldersAsync() =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<IReadOnlyList<MediaItem>> GetItemsAsync(IReadOnlyCollection<string> folders) =>
        Task.FromResult<IReadOnlyList<MediaItem>>([]);

    public Task<IReadOnlyList<MediaItem>> GetAllItemsAsync() =>
        Task.FromResult<IReadOnlyList<MediaItem>>([]);

    public Task<byte[]?> GetThumbnailAsync(MediaItem item, int size) =>
        Task.FromResult<byte[]?>(null);

    public Task<Stream> OpenReadAsync(MediaItem item) =>
        throw new NotSupportedException("Media library no disponible en esta plataforma.");
}
