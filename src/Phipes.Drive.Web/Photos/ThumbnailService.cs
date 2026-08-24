using System.Collections.Concurrent;
using Phipes.Drive.Core.Photos;
using Phipes.Drive.Core.Storage;

namespace Phipes.Drive.Web.Photos;

/// <summary>
/// Owns thumbnail files: returns one if it is already on disk, otherwise generates every size on
/// the spot. Generation is serialised per hash so a burst of requests decodes the original once.
/// </summary>
public sealed class ThumbnailService(
    IFileStorage storage,
    IThumbnailGenerator generator,
    IServiceScopeFactory scopeFactory,
    string thumbsRoot,
    ILogger<ThumbnailService> logger)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public string ThumbsRoot { get; } = thumbsRoot;

    /// <summary>Absolute path of an existing thumbnail, or null when it cannot be produced.</summary>
    public async Task<string?> EnsureAsync(PhotoItem item, int size, CancellationToken ct = default)
    {
        var path = ThumbnailPaths.For(ThumbsRoot, item.Hash, size);
        if (File.Exists(path)) return path;
        if (item.ThumbFailed) return null;

        var gate = _locks.GetOrAdd(item.Hash, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(path)) return path;

            var kind = MediaTypes.Classify(item.Name);
            var physicalPath = (storage as ILocalFileStorage)?.TryGetPhysicalPath(item.Path);
            var generated = await generator.GenerateAsync(
                item.Hash, kind,
                token => storage.OpenReadAsync(item.Path, token),
                physicalPath, ct).ConfigureAwait(false);

            item.HasThumb = generated;
            item.ThumbFailed = !generated;
            await RecordAsync(item.Hash, generated, ct).ConfigureAwait(false);

            if (!generated)
            {
                logger.LogInformation("No thumbnail could be produced for {Path}", item.Path);
                return null;
            }

            return File.Exists(path) ? path : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Thumbnail generation failed for {Path}", item.Path);
            await RecordAsync(item.Hash, false, ct).ConfigureAwait(false);
            return null;
        }
        finally
        {
            gate.Release();
            _locks.TryRemove(new KeyValuePair<string, SemaphoreSlim>(item.Hash, gate));
        }
    }

    /// <summary>Generates the thumbnails of one indexed item, looked up by hash.</summary>
    public async Task GenerateAsync(string hash, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IPhotoStore>();
        if (await store.GetAsync(hash, ct).ConfigureAwait(false) is { } item)
            await EnsureAsync(item, ThumbnailPaths.SmallSize, ct).ConfigureAwait(false);
    }

    private async Task RecordAsync(string hash, bool generated, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IPhotoStore>();
            await store.SetThumbStateAsync(hash, generated, !generated, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not persist the thumbnail state of {Hash}", hash);
        }
    }
}
