using Microsoft.EntityFrameworkCore;
using Phipes.Drive.Core.Photos;
using Phipes.Drive.Web.Data;

namespace Phipes.Drive.Web.Photos;

/// <summary>One entry of the gallery timeline, as returned by <c>GET /api/photos</c>.</summary>
public sealed record PhotoDto(
    string Id,
    string Name,
    string Path,
    DateTimeOffset TakenUtc,
    long SizeBytes,
    int? Width,
    int? Height,
    bool IsVideo,
    long? DurationMs,
    bool HasThumb);

public sealed record PhotoPage(int Total, IReadOnlyList<PhotoDto> Items);

/// <summary>
/// Read side of the photo index, shared by the JSON API and the Blazor gallery. It owns its own
/// scope per call so it can be a singleton and is safe to use from a long-lived circuit.
/// </summary>
public sealed class PhotoQueryService(IServiceScopeFactory scopeFactory)
{
    public const int MaxTake = 500;

    /// <param name="since">
    /// Optional lower bound on <c>IndexedUtc</c>: returns only what the server learned about after
    /// that instant, which is what a client needs to sync a cached timeline.
    /// </param>
    public async Task<PhotoPage> ListAsync(
        DateTimeOffset? since, int skip, int take, CancellationToken ct = default)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, MaxTake);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var query = db.Photos.AsNoTracking();
        if (since is { } lowerBound) query = query.Where(p => p.IndexedUtc > lowerBound);

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var items = await query
            .OrderByDescending(p => p.TakenUtc)
            .ThenBy(p => p.Hash)
            .Skip(skip)
            .Take(take)
            .Select(p => new PhotoDto(p.Hash, p.Name, p.Path, p.TakenUtc, p.SizeBytes,
                p.Width, p.Height, p.IsVideo, p.DurationMs, p.HasThumb))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new PhotoPage(total, items);
    }

    public async Task<PhotoItem?> GetAsync(string hash, CancellationToken ct = default)
    {
        if (!ThumbnailPaths.IsValidHash(hash)) return null;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Photos.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Hash == hash, ct)
            .ConfigureAwait(false);
    }

    public async Task<PhotoScanState> GetScanStateAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PhotoScanStates.AsNoTracking()
                   .FirstOrDefaultAsync(s => s.Id == PhotoScanState.SingletonId, ct)
                   .ConfigureAwait(false)
               ?? new PhotoScanState();
    }
}
