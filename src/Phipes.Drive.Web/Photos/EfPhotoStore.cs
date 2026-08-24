using Microsoft.EntityFrameworkCore;
using Phipes.Drive.Core.Photos;
using Phipes.Drive.Web.Data;

namespace Phipes.Drive.Web.Photos;

/// <summary>EF Core implementation of the photo index over the existing SQLite database.</summary>
public sealed class EfPhotoStore(AppDbContext db) : IPhotoStore
{
    public async Task<IReadOnlyList<PhotoIndexEntry>> GetIndexSnapshotAsync(CancellationToken ct = default) =>
        await db.Photos
            .AsNoTracking()
            .Select(p => new PhotoIndexEntry(p.Hash, p.Path, p.SizeBytes, p.ModifiedUtc))
            .ToListAsync(ct)
            .ConfigureAwait(false);

    public Task<PhotoItem?> GetAsync(string hash, CancellationToken ct = default) =>
        db.Photos.AsNoTracking().FirstOrDefaultAsync(p => p.Hash == hash, ct);

    public async Task UpsertAsync(PhotoItem item, CancellationToken ct = default)
    {
        var existing = await db.Photos.FirstOrDefaultAsync(p => p.Hash == item.Hash, ct).ConfigureAwait(false);
        if (existing is null)
        {
            db.Photos.Add(item);
        }
        else
        {
            existing.Path = item.Path;
            existing.Name = item.Name;
            existing.SizeBytes = item.SizeBytes;
            existing.ModifiedUtc = item.ModifiedUtc;
            existing.TakenUtc = item.TakenUtc;
            existing.TakenFromExif = item.TakenFromExif;
            existing.Width = item.Width;
            existing.Height = item.Height;
            existing.Latitude = item.Latitude;
            existing.Longitude = item.Longitude;
            existing.IsVideo = item.IsVideo;
            existing.DurationMs = item.DurationMs;
            existing.IndexedUtc = item.IndexedUtc;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        // A full scan can touch tens of thousands of rows; do not let the tracker grow with them.
        db.ChangeTracker.Clear();
    }

    public async Task RemoveAsync(IReadOnlyCollection<string> hashes, CancellationToken ct = default)
    {
        if (hashes.Count == 0) return;

        // Chunked so a large cleanup never blows past SQLite's parameter limit.
        foreach (var chunk in hashes.Chunk(400))
            await db.Photos.Where(p => chunk.Contains(p.Hash)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    public async Task SetThumbStateAsync(string hash, bool hasThumb, bool failed, CancellationToken ct = default) =>
        await db.Photos
            .Where(p => p.Hash == hash)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.HasThumb, hasThumb)
                                      .SetProperty(p => p.ThumbFailed, failed), ct)
            .ConfigureAwait(false);

    public async Task<PhotoScanState> GetScanStateAsync(CancellationToken ct = default) =>
        await db.PhotoScanStates.FirstOrDefaultAsync(s => s.Id == PhotoScanState.SingletonId, ct)
            .ConfigureAwait(false)
        ?? new PhotoScanState();

    public async Task SaveScanStateAsync(PhotoScanState state, CancellationToken ct = default)
    {
        state.Id = PhotoScanState.SingletonId;
        var exists = await db.PhotoScanStates
            .AnyAsync(s => s.Id == PhotoScanState.SingletonId, ct).ConfigureAwait(false);

        if (exists) db.PhotoScanStates.Update(state);
        else db.PhotoScanStates.Add(state);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
