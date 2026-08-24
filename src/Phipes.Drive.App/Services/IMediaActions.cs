namespace Phipes.Drive.App.Services;

/// <summary>Hands media to other apps: the system share sheet and the system viewer/player.</summary>
public interface IMediaActions
{
    /// <summary>Shares a file that lives in the app cache (exposed through the FileProvider).</summary>
    Task ShareFileAsync(string filePath, string name, bool isVideo);

    /// <summary>Shares an item that is already on the device, without copying it.</summary>
    Task ShareDeviceItemAsync(MediaItem item);

    /// <summary>Opens a cached file with the system viewer/player.</summary>
    Task OpenFileAsync(string filePath, bool isVideo);

    /// <summary>Opens a device item with the system viewer/player.</summary>
    Task OpenDeviceItemAsync(MediaItem item);
}

/// <summary>No-op fallback for platforms without an implementation yet (iOS pending).</summary>
public sealed class NullMediaActions : IMediaActions
{
    public Task ShareFileAsync(string filePath, string name, bool isVideo) => Task.CompletedTask;

    public Task ShareDeviceItemAsync(MediaItem item) => Task.CompletedTask;

    public Task OpenFileAsync(string filePath, bool isVideo) => Task.CompletedTask;

    public Task OpenDeviceItemAsync(MediaItem item) => Task.CompletedTask;
}
