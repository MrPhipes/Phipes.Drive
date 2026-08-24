using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phipes.Drive.Core.Storage;

namespace Phipes.Drive.Core.Photos;

public sealed record PhotoScanResult(int FilesSeen, int Indexed, int Removed, int Duplicates, TimeSpan Elapsed);

/// <summary>
/// Walks the storage tree and keeps the photo index in sync. Incremental by design: a file whose
/// path, size and modification time already match the index is never hashed again.
/// </summary>
public sealed class PhotoIndexer(
    IFileStorage storage,
    IPhotoStore store,
    IPhotoMetadataReader metadataReader,
    IThumbnailQueue thumbnails,
    ILogger<PhotoIndexer>? logger = null,
    string? excludedPhysicalRoot = null)
{
    /// <summary>Tolerance when comparing stored and on-disk timestamps, to survive storage precision.</summary>
    private static readonly TimeSpan MtimeTolerance = TimeSpan.FromSeconds(1);

    private readonly ILogger _log = logger ?? NullLogger<PhotoIndexer>.Instance;

    private readonly string? _excludedRoot = excludedPhysicalRoot is null
        ? null
        : Path.GetFullPath(excludedPhysicalRoot) + Path.DirectorySeparatorChar;

    public async Task<PhotoScanResult> ScanAsync(CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        var snapshot = await store.GetIndexSnapshotAsync(ct).ConfigureAwait(false);
        var byPath = new Dictionary<string, PhotoIndexEntry>(StringComparer.OrdinalIgnoreCase);
        var byHash = new Dictionary<string, PhotoIndexEntry>(StringComparer.Ordinal);
        foreach (var entry in snapshot)
        {
            byPath[entry.Path] = entry;
            byHash[entry.Hash] = entry;
        }

        var seenHashes = new HashSet<string>(StringComparer.Ordinal);
        int filesSeen = 0, indexed = 0, duplicates = 0;

        await foreach (var file in storage.EnumerateFilesAsync(string.Empty, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            var kind = MediaTypes.Classify(file.Name);
            if (kind == MediaKind.None) continue;
            if (IsExcluded(file.RelativePath)) continue;
            filesSeen++;

            // Fast path: same path, same size, same mtime, so the bytes cannot have changed.
            var hasKnownRow = byPath.TryGetValue(file.RelativePath, out var known);
            if (hasKnownRow && IsUnchanged(known, file))
            {
                seenHashes.Add(known.Hash);
                continue;
            }

            string hash;
            try
            {
                hash = await ComputeHashAsync(file.RelativePath, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(e, "Skipping unreadable media file {Path}", file.RelativePath);
                continue;
            }

            // Same bytes already indexed under another path: keep a single row for the first copy.
            if (byHash.TryGetValue(hash, out var existing) && !PathEquals(existing.Path, file.RelativePath))
            {
                if (seenHashes.Contains(hash) || await StillExistsAsync(existing.Path, ct).ConfigureAwait(false))
                {
                    duplicates++;
                    seenHashes.Add(hash);
                    continue;
                }
            }

            // The content at this path changed, so its previous row is now orphaned.
            if (hasKnownRow && known.Hash != hash)
            {
                await store.RemoveAsync([known.Hash], ct).ConfigureAwait(false);
                byHash.Remove(known.Hash);
                seenHashes.Remove(known.Hash);
            }

            var item = await BuildItemAsync(file, kind, hash, startedAt, ct).ConfigureAwait(false);
            await store.UpsertAsync(item, ct).ConfigureAwait(false);

            var row = new PhotoIndexEntry(hash, item.Path, item.SizeBytes, item.ModifiedUtc);
            byHash[hash] = row;
            byPath[item.Path] = row;
            seenHashes.Add(hash);
            indexed++;

            thumbnails.Enqueue(hash);
        }

        var gone = snapshot.Select(e => e.Hash).Where(h => !seenHashes.Contains(h)).ToArray();
        if (gone.Length > 0) await store.RemoveAsync(gone, ct).ConfigureAwait(false);

        stopwatch.Stop();
        var state = await store.GetScanStateAsync(ct).ConfigureAwait(false);
        state.LastScanUtc = startedAt;
        state.LastScanDurationMs = (long)stopwatch.Elapsed.TotalMilliseconds;
        state.LastScanFileCount = filesSeen;
        state.LastScanIndexedCount = indexed;
        await store.SaveScanStateAsync(state, ct).ConfigureAwait(false);

        _log.LogInformation(
            "Photo scan finished: {Files} media files, {Indexed} indexed, {Duplicates} duplicates, {Removed} removed in {Elapsed}",
            filesSeen, indexed, duplicates, gone.Length, stopwatch.Elapsed);

        return new PhotoScanResult(filesSeen, indexed, gone.Length, duplicates, stopwatch.Elapsed);
    }

    /// <summary>True when the indexed row already describes exactly this file.</summary>
    public static bool IsUnchanged(PhotoIndexEntry known, StorageItem file) =>
        known.SizeBytes == file.SizeBytes &&
        (known.ModifiedUtc - file.ModifiedUtc).Duration() <= MtimeTolerance;

    private static bool PathEquals(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private bool IsExcluded(string relativePath)
    {
        if (_excludedRoot is null) return false;
        var physical = (storage as ILocalFileStorage)?.TryGetPhysicalPath(relativePath);
        return physical is not null && physical.StartsWith(_excludedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> StillExistsAsync(string relativePath, CancellationToken ct)
    {
        try
        {
            return await storage.StatAsync(relativePath, ct).ConfigureAwait(false) is { IsFolder: false };
        }
        catch (StoragePathException)
        {
            return false;
        }
    }

    private async Task<string> ComputeHashAsync(string relativePath, CancellationToken ct)
    {
        await using var stream = await storage.OpenReadAsync(relativePath, ct).ConfigureAwait(false);
        var digest = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(digest);
    }

    private async Task<PhotoItem> BuildItemAsync(
        StorageItem file, MediaKind kind, string hash, DateTimeOffset indexedUtc, CancellationToken ct)
    {
        var physicalPath = (storage as ILocalFileStorage)?.TryGetPhysicalPath(file.RelativePath);
        var metadata = await metadataReader.ReadAsync(
            token => storage.OpenReadAsync(file.RelativePath, token),
            file.Name, kind, file.ModifiedUtc, physicalPath, ct).ConfigureAwait(false);

        return new PhotoItem
        {
            Hash = hash,
            Path = file.RelativePath,
            Name = file.Name,
            SizeBytes = file.SizeBytes,
            ModifiedUtc = file.ModifiedUtc,
            TakenUtc = metadata.TakenUtc,
            TakenFromExif = metadata.TakenFromExif,
            Width = metadata.Width,
            Height = metadata.Height,
            Latitude = metadata.Latitude,
            Longitude = metadata.Longitude,
            IsVideo = kind == MediaKind.Video,
            DurationMs = metadata.DurationMs,
            HasThumb = false,
            ThumbFailed = false,
            IndexedUtc = indexedUtc,
        };
    }
}
