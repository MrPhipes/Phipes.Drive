namespace Phipes.Drive.Core.Storage;

/// <summary>
/// Implemented by storage backends whose items are real files on disk, so external tools
/// (ffmpeg) can be pointed at them directly instead of streaming the bytes twice.
/// </summary>
public interface ILocalFileStorage
{
    /// <summary>Absolute path of a stored item, or null when the path is invalid or absent.</summary>
    string? TryGetPhysicalPath(string relativePath);
}
