using Phipes.Drive.Core.Photos;

namespace Phipes.Drive.Tests;

/// <summary>In-memory <see cref="IPhotoStore"/> so the indexer can be tested without a database.</summary>
public sealed class FakePhotoStore : IPhotoStore
{
    public Dictionary<string, PhotoItem> Items { get; } = new(StringComparer.Ordinal);

    public PhotoScanState State { get; private set; } = new();

    public Task<IReadOnlyList<PhotoIndexEntry>> GetIndexSnapshotAsync(CancellationToken ct = default)
    {
        IReadOnlyList<PhotoIndexEntry> snapshot = Items.Values
            .Select(i => new PhotoIndexEntry(i.Hash, i.Path, i.SizeBytes, i.ModifiedUtc))
            .ToList();
        return Task.FromResult(snapshot);
    }

    public Task<PhotoItem?> GetAsync(string hash, CancellationToken ct = default) =>
        Task.FromResult(Items.GetValueOrDefault(hash));

    public Task UpsertAsync(PhotoItem item, CancellationToken ct = default)
    {
        Items[item.Hash] = item;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(IReadOnlyCollection<string> hashes, CancellationToken ct = default)
    {
        foreach (var hash in hashes) Items.Remove(hash);
        return Task.CompletedTask;
    }

    public Task SetThumbStateAsync(string hash, bool hasThumb, bool failed, CancellationToken ct = default)
    {
        if (Items.TryGetValue(hash, out var item))
        {
            item.HasThumb = hasThumb;
            item.ThumbFailed = failed;
        }
        return Task.CompletedTask;
    }

    public Task<PhotoScanState> GetScanStateAsync(CancellationToken ct = default) => Task.FromResult(State);

    public Task SaveScanStateAsync(PhotoScanState state, CancellationToken ct = default)
    {
        State = state;
        return Task.CompletedTask;
    }
}

/// <summary>Metadata reader that only echoes the file timestamp and counts how often it ran.</summary>
public sealed class CountingMetadataReader : IPhotoMetadataReader
{
    public int Calls { get; private set; }

    public Task<PhotoMetadata> ReadAsync(
        Func<CancellationToken, Task<Stream>> openRead,
        string fileName,
        MediaKind kind,
        DateTimeOffset fileModifiedUtc,
        string? physicalPath,
        CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(new PhotoMetadata(fileModifiedUtc, false, null, null, null, null, null));
    }
}
