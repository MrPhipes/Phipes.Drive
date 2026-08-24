namespace Phipes.Drive.Core.Photos;

/// <summary>
/// Lightweight snapshot row used by the scanner to decide whether a file needs reprocessing,
/// without loading whole <see cref="PhotoItem"/> entities.
/// </summary>
public readonly record struct PhotoIndexEntry(
    string Hash,
    string Path,
    long SizeBytes,
    DateTimeOffset ModifiedUtc);

/// <summary>Persistence seam for the photo index, so the scanner can be tested without a database.</summary>
public interface IPhotoStore
{
    /// <summary>All indexed items, as the minimal fields the incremental scan needs.</summary>
    Task<IReadOnlyList<PhotoIndexEntry>> GetIndexSnapshotAsync(CancellationToken ct = default);

    Task<PhotoItem?> GetAsync(string hash, CancellationToken ct = default);

    /// <summary>Inserts the item, or updates the existing row with the same hash.</summary>
    Task UpsertAsync(PhotoItem item, CancellationToken ct = default);

    /// <summary>Drops rows whose hash is in <paramref name="hashes"/> (files that disappeared).</summary>
    Task RemoveAsync(IReadOnlyCollection<string> hashes, CancellationToken ct = default);

    /// <summary>Records the outcome of a thumbnail generation attempt.</summary>
    Task SetThumbStateAsync(string hash, bool hasThumb, bool failed, CancellationToken ct = default);

    Task<PhotoScanState> GetScanStateAsync(CancellationToken ct = default);

    Task SaveScanStateAsync(PhotoScanState state, CancellationToken ct = default);
}
