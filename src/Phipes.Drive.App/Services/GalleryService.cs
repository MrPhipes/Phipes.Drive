namespace Phipes.Drive.App.Services;

/// <summary>Outcome of building the timeline, including why the NAS side may be missing.</summary>
/// <param name="Items">Merged timeline, newest first.</param>
/// <param name="RemoteAvailable">False when the server index could not be read this time.</param>
/// <param name="RemoteNotice">Short Spanish note to show discreetly when the NAS is missing.</param>
public sealed record GalleryResult(
    IReadOnlyList<GalleryItem> Items,
    bool RemoteAvailable,
    string? RemoteNotice);

/// <summary>
/// Builds the unified timeline: everything on the phone plus everything indexed on the NAS,
/// merged by file name and exact size so a photo that exists in both places appears once.
/// The device side always wins: if the server is unreachable the timeline still renders.
/// </summary>
public sealed class GalleryService(IMediaLibrary media, RemotePhotoIndex index, BackupLedger ledger)
{
    /// <summary>Local items only. Fast, no network, safe to call before the server answers.</summary>
    public async Task<IReadOnlyList<GalleryItem>> LoadLocalAsync()
    {
        var local = await media.GetAllItemsAsync();
        return Merge(local, [], ledger.Keys);
    }

    /// <summary>
    /// Full timeline. Uses the cached server index immediately and, when
    /// <paramref name="refreshRemote"/> is set, also asks the server for what is new.
    /// </summary>
    public async Task<GalleryResult> LoadAsync(bool refreshRemote, CancellationToken ct)
    {
        var localTask = media.GetAllItemsAsync();
        var remote = await index.LoadCachedAsync();

        var available = true;
        string? notice = null;
        if (refreshRemote)
        {
            try
            {
                remote = await index.RefreshAsync(ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SessionExpiredException)
            {
                available = false;
                notice = "Sesión expirada: solo se muestran las fotos del teléfono.";
            }
            catch (PhotoIndexUnavailableException)
            {
                available = false;
                notice = "Este servidor todavía no publica el índice de fotos.";
            }
            catch (Exception)
            {
                available = false;
                notice = remote.Count > 0
                    ? "Sin conexión con el servidor: la parte del NAS puede estar desactualizada."
                    : "Sin conexión con el servidor: solo se muestran las fotos del teléfono.";
            }
        }

        var local = await localTask;
        return new GalleryResult(Merge(local, remote, ledger.Keys), available, notice);
    }

    private static IReadOnlyList<GalleryItem> Merge(
        IReadOnlyList<MediaItem> local,
        IReadOnlyList<PhotoDto> remote,
        IReadOnlyCollection<string> backedUpKeys)
    {
        var backedUp = backedUpKeys as HashSet<string> ??
                       new HashSet<string>(backedUpKeys, StringComparer.OrdinalIgnoreCase);

        var byKey = new Dictionary<string, GalleryItem>(
            local.Count + remote.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var item in local)
        {
            var key = BackupLedger.KeyFor(item.Name, item.SizeBytes);
            if (byKey.ContainsKey(key))
                continue;
            byKey[key] = new GalleryItem
            {
                Key = key,
                Name = item.Name,
                SizeBytes = item.SizeBytes,
                TakenLocal = item.TakenLocal,
                IsVideo = item.IsVideo,
                Local = item,
                KnownBackedUp = backedUp.Contains(key),
            };
        }

        foreach (var photo in remote)
        {
            var key = BackupLedger.KeyFor(photo.Name, photo.SizeBytes);
            if (byKey.TryGetValue(key, out var existing))
            {
                if (existing.RemoteId is not null)
                    continue;
                byKey[key] = new GalleryItem
                {
                    Key = key,
                    Name = existing.Name,
                    SizeBytes = existing.SizeBytes,
                    // Prefer the device capture date: MediaStore knows the local timezone.
                    TakenLocal = existing.Local is not null
                        ? existing.TakenLocal
                        : photo.TakenUtc.ToLocalTime().DateTime,
                    IsVideo = existing.IsVideo || photo.IsVideo,
                    Local = existing.Local,
                    RemoteId = photo.Id,
                    DurationMs = photo.DurationMs ?? 0,
                    KnownBackedUp = existing.KnownBackedUp,
                };
                continue;
            }

            byKey[key] = new GalleryItem
            {
                Key = key,
                Name = photo.Name,
                SizeBytes = photo.SizeBytes,
                TakenLocal = photo.TakenUtc.ToLocalTime().DateTime,
                IsVideo = photo.IsVideo,
                RemoteId = photo.Id,
                DurationMs = photo.DurationMs ?? 0,
            };
        }

        var merged = byKey.Values.ToList();
        merged.Sort((a, b) => b.TakenLocal.CompareTo(a.TakenLocal));
        return merged;
    }
}
