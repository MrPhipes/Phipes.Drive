using System.Text.Json;

namespace Phipes.Drive.App.Services;

/// <summary>
/// Local copy of the server photo index (/api/photos). It is persisted to the cache
/// directory so the gallery can paint a full timeline before the network answers -- and
/// keep painting it when the server is down.
/// </summary>
public sealed class RemotePhotoIndex(ApiClient api)
{
    private const int PageSize = 200;

    /// <summary>Hard cap so a huge library can never blow up memory or the cache file.</summary>
    private const int MaxItems = 50_000;

    /// <summary>A full re-read is done this often so server-side deletions eventually show up.</summary>
    private static readonly TimeSpan FullRefreshEvery = TimeSpan.FromDays(3);

    private const string FullRefreshKey = "gallery.index_full_refresh_unix";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _cachePath =
        Path.Combine(FileSystem.CacheDirectory, "remote-photo-index.json");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<PhotoDto>? _items;

    /// <summary>Items known from the last successful read (possibly from disk). Newest first.</summary>
    public IReadOnlyList<PhotoDto> Items => _items ?? [];

    /// <summary>Loads the on-disk copy. Never throws.</summary>
    public async Task<IReadOnlyList<PhotoDto>> LoadCachedAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_items is not null)
                return _items;
            try
            {
                if (File.Exists(_cachePath))
                {
                    await using var stream = File.OpenRead(_cachePath);
                    _items = await JsonSerializer.DeserializeAsync<List<PhotoDto>>(stream, Json);
                }
            }
            catch (Exception)
            {
                // Corrupt cache: drop it and rebuild from the server.
                _items = null;
            }
            return _items ??= [];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Brings the index up to date. Reads everything the first time (and every few days),
    /// otherwise only items newer than the newest one already known.
    /// </summary>
    /// <returns>The refreshed items.</returns>
    /// <exception cref="ApiException">The server could not be reached or refused the request.</exception>
    public async Task<IReadOnlyList<PhotoDto>> RefreshAsync(CancellationToken ct)
    {
        await LoadCachedAsync();
        await _gate.WaitAsync(ct);
        try
        {
            var known = _items ?? [];
            var full = known.Count == 0 || IsFullRefreshDue();
            var since = full || known.Count == 0
                ? (DateTimeOffset?)null
                : known.Max(i => i.TakenUtc);

            var fetched = new List<PhotoDto>();
            var skip = 0;
            while (fetched.Count < MaxItems)
            {
                var page = await api.GetPhotosAsync(since, skip, PageSize, ct);
                if (page.Items.Count == 0)
                    break;
                fetched.AddRange(page.Items);
                skip += page.Items.Count;
                if (page.Total > 0 && skip >= page.Total)
                    break;
            }

            List<PhotoDto> merged;
            if (full)
            {
                merged = fetched;
                Preferences.Default.Set(FullRefreshKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            }
            else
            {
                var byId = new Dictionary<string, PhotoDto>(known.Count + fetched.Count);
                foreach (var item in known)
                    byId[item.Id] = item;
                foreach (var item in fetched)
                    byId[item.Id] = item;
                merged = byId.Values.ToList();
            }

            merged.Sort((a, b) => b.TakenUtc.CompareTo(a.TakenUtc));
            if (merged.Count > MaxItems)
                merged.RemoveRange(MaxItems, merged.Count - MaxItems);
            _items = merged;
            await SaveAsync(merged);
            return merged;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Clear()
    {
        _items = null;
        Preferences.Default.Remove(FullRefreshKey);
        try
        {
            if (File.Exists(_cachePath))
                File.Delete(_cachePath);
        }
        catch (IOException)
        {
        }
    }

    private static bool IsFullRefreshDue()
    {
        var unix = Preferences.Default.Get(FullRefreshKey, 0L);
        return unix == 0 ||
               DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unix) > FullRefreshEvery;
    }

    private async Task SaveAsync(List<PhotoDto> items)
    {
        try
        {
            var temp = _cachePath + ".tmp";
            await using (var stream = File.Create(temp))
                await JsonSerializer.SerializeAsync(stream, items, Json);
            File.Move(temp, _cachePath, overwrite: true);
        }
        catch (Exception)
        {
            // The cache is an optimisation; losing it only costs one extra full read.
        }
    }
}
