namespace Phipes.Drive.App.Services;

/// <summary>
/// Pulls full-size originals out of the NAS into the app cache so they can be viewed,
/// played or shared. Downloads resume with a Range request when they were interrupted.
/// </summary>
public sealed class OriginalStore(ApiClient api)
{
    /// <summary>Cache budget for downloaded originals before the oldest ones are dropped.</summary>
    private const long MaxCacheBytes = 1024L * 1024 * 1024;

    private readonly string _directory = Path.Combine(FileSystem.CacheDirectory, "originals");

    /// <summary>Where the original of this item lands once downloaded.</summary>
    public string PathFor(GalleryItem item)
    {
        var id = item.RemoteId ?? item.Key;
        var stem = id.Length > 32 ? id[..32] : id;
        return Path.Combine(_directory, $"{stem}-{SafeName(item.Name)}");
    }

    public bool IsDownloaded(GalleryItem item)
    {
        var path = PathFor(item);
        return File.Exists(path) &&
               (item.SizeBytes <= 0 || new FileInfo(path).Length == item.SizeBytes);
    }

    /// <summary>
    /// Ensures the original is on disk and returns its path. Reports progress in 0..1.
    /// </summary>
    /// <exception cref="InvalidOperationException">The item has no copy on the NAS.</exception>
    public async Task<string> EnsureAsync(
        GalleryItem item, IProgress<double>? progress, CancellationToken ct)
    {
        if (item.RemoteId is null)
            throw new InvalidOperationException("El elemento no está en el NAS.");

        var path = PathFor(item);
        if (IsDownloaded(item))
        {
            progress?.Report(1d);
            return path;
        }

        Directory.CreateDirectory(_directory);
        await api.DownloadOriginalAsync(item.RemoteId, path, item.SizeBytes, progress, ct);
        _ = Task.Run(Trim, CancellationToken.None);
        return path;
    }

    private static string SafeName(string name)
    {
        Span<char> buffer = stackalloc char[Math.Min(name.Length, 80)];
        for (var i = 0; i < buffer.Length; i++)
        {
            var c = name[i];
            buffer[i] = char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_';
        }
        var safe = new string(buffer);
        return safe.Length == 0 ? "archivo" : safe;
    }

    /// <summary>Deletes the oldest downloads once the cache grows past its budget.</summary>
    public void Trim()
    {
        try
        {
            var directory = new DirectoryInfo(_directory);
            if (!directory.Exists)
                return;
            var files = directory.GetFiles();
            var total = files.Sum(f => f.Length);
            if (total <= MaxCacheBytes)
                return;
            foreach (var file in files.OrderBy(f => f.LastAccessTimeUtc))
            {
                if (total <= MaxCacheBytes)
                    break;
                total -= file.Length;
                try
                {
                    file.Delete();
                }
                catch (IOException)
                {
                }
            }
        }
        catch (Exception)
        {
        }
    }
}
