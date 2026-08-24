namespace Phipes.Drive.App.Services;

/// <summary>
/// Supplies thumbnail bytes for the gallery. Device items come from the MediaStore
/// thumbnail cache (no network); NAS-only items come from /api/photos/{id}/thumb and are
/// kept on disk under the app cache directory. Server thumbnails are immutable per id, so
/// the cache never needs invalidation -- only trimming.
/// </summary>
public sealed class ThumbnailStore(ApiClient api, IMediaLibrary media)
{
    /// <summary>Roughly how much disk the thumbnail cache may use before old files are dropped.</summary>
    private const long MaxCacheBytes = 192L * 1024 * 1024;

    private readonly string _directory = Path.Combine(FileSystem.CacheDirectory, "thumbs");

    // Keep the network from starving the UI thread pool; the device side is cheap but not free.
    private readonly SemaphoreSlim _remoteGate = new(4, 4);
    private readonly SemaphoreSlim _localGate = new(3, 3);
    private int _trimmed;

    public async Task<byte[]?> GetAsync(GalleryItem item, int size, CancellationToken ct)
    {
        if (item.Local is not null)
        {
            await _localGate.WaitAsync(ct);
            try
            {
                return await media.GetThumbnailAsync(item.Local, size);
            }
            finally
            {
                _localGate.Release();
            }
        }

        return item.RemoteId is null ? null : await GetRemoteAsync(item.RemoteId, size, ct);
    }

    /// <summary>Server thumbnail for an id, from disk when it was fetched before.</summary>
    public async Task<byte[]?> GetRemoteAsync(string id, int size, CancellationToken ct)
    {
        var path = PathFor(id, size);
        try
        {
            if (File.Exists(path))
                return await File.ReadAllBytesAsync(path, ct);
        }
        catch (IOException)
        {
            // Fall through and refetch.
        }

        await _remoteGate.WaitAsync(ct);
        try
        {
            var bytes = await api.GetThumbnailAsync(id, size, ct);
            await WriteAsync(path, bytes, ct);
            return bytes;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Offline or the server has no thumbnail: the grid shows a placeholder tile.
            return null;
        }
        finally
        {
            _remoteGate.Release();
        }
    }

    private string PathFor(string id, int size)
    {
        // The id is a hex SHA-256, so it is already a safe file name.
        var safe = id.Length > 64 ? id[..64] : id;
        return Path.Combine(_directory, $"{safe}_{size}.jpg");
    }

    private async Task WriteAsync(string path, byte[] bytes, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            await File.WriteAllBytesAsync(path, bytes, ct);
            if (Interlocked.Exchange(ref _trimmed, 1) == 0)
                _ = Task.Run(Trim, CancellationToken.None);
        }
        catch (IOException)
        {
            // A full disk only costs a re-download later.
        }
    }

    /// <summary>Drops the least recently used thumbnails once the cache grows too large.</summary>
    private void Trim()
    {
        try
        {
            var directory = new DirectoryInfo(_directory);
            if (!directory.Exists)
                return;
            var files = directory.GetFiles("*.jpg");
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
