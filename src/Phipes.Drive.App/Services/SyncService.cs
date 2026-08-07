namespace Phipes.Drive.App.Services;

public sealed record SyncProgress(
    int FileIndex,
    int FileCount,
    string FileName,
    long FileBytesSent,
    long FileBytesTotal,
    string Status);

public sealed record SyncSummary(int Uploaded, int Resumed, int Skipped, int Errors, IReadOnlyList<string> ErrorDetails)
{
    public override string ToString() =>
        $"Subidos {Uploaded} · Reanudados {Resumed} · Saltados {Skipped} · Errores {Errors}";
}

/// <summary>
/// Backs up device photos/videos to the server. For each item: stat the remote path;
/// skip when the size matches, resume with append when the remote file is shorter,
/// otherwise upload in 64 MB chunks (first chunk creates the file, the rest append).
/// </summary>
public sealed class SyncService(ApiClient api, IMediaLibrary media, BackupSettings settings)
{
    /// <summary>Chunk size per request. Must stay under the 100 MB per-request limit of the server edge.</summary>
    public const long ChunkSize = 64L * 1024 * 1024;

    private int _running;

    /// <summary>True while a sync (manual or background) is in progress.</summary>
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>True when the current network satisfies the Wi-Fi-only preference.</summary>
    public bool IsNetworkAllowed()
    {
        var connectivity = Connectivity.Current;
        if (connectivity.NetworkAccess != NetworkAccess.Internet)
            return false;
        return !settings.WifiOnly ||
               connectivity.ConnectionProfiles.Contains(ConnectionProfile.WiFi);
    }

    /// <summary>
    /// Runs a full backup pass. Returns null when another sync is already running.
    /// Throws <see cref="SessionExpiredException"/> when the stored session is invalid.
    /// </summary>
    public async Task<SyncSummary?> RunAsync(IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
            return null;
        try
        {
            var summary = await RunCoreAsync(progress, ct);
            settings.LastRunUtc = DateTimeOffset.UtcNow;
            settings.LastResult = summary.ToString();
            return summary;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<SyncSummary> RunCoreAsync(IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        var items = await media.GetItemsAsync(settings.Folders.ToList());
        int uploaded = 0, resumed = 0, skipped = 0;
        var errors = new List<string>();

        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[i];
            var remotePath = RemotePathFor(item);
            void Report(long sent, string status) =>
                progress?.Report(new SyncProgress(i + 1, items.Count, item.Name, sent, item.SizeBytes, status));

            try
            {
                Report(0, "Consultando…");
                var stat = await api.StatAsync(remotePath, ct);

                if (stat.Exists && stat.IsFolder == true)
                {
                    errors.Add($"{item.Name}: la ruta remota es una carpeta.");
                    continue;
                }
                if (stat.Exists && stat.SizeBytes == item.SizeBytes)
                {
                    skipped++;
                    Report(item.SizeBytes, "Ya respaldado");
                    continue;
                }
                if (stat.Exists && stat.SizeBytes > item.SizeBytes)
                {
                    // Remote is larger than local: different content; do not touch it.
                    errors.Add($"{item.Name}: el remoto es más grande que el local; se omitió.");
                    continue;
                }

                var offset = stat.Exists ? stat.SizeBytes ?? 0 : 0;
                await UploadAsync(item, remotePath, offset, Report, ct);
                if (offset > 0) resumed++; else uploaded++;
                Report(item.SizeBytes, "Completado");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SessionExpiredException)
            {
                throw;
            }
            catch (QuotaExceededException ex)
            {
                errors.Add($"{item.Name}: {ex.Message}");
                // No point continuing when the server is full.
                errors.Add("Respaldo detenido: cuota llena.");
                break;
            }
            catch (Exception ex)
            {
                errors.Add($"{item.Name}: {ex.Message}");
            }
        }

        return new SyncSummary(uploaded, resumed, skipped, errors.Count, errors);
    }

    /// <summary>Remote layout: Camera/&lt;yyyy&gt;/&lt;MM&gt;/&lt;name&gt; by capture date.</summary>
    public static string RemotePathFor(MediaItem item) =>
        $"Camera/{item.TakenLocal:yyyy}/{item.TakenLocal:MM}/{item.Name}";

    private async Task UploadAsync(
        MediaItem item, string remotePath, long offset, Action<long, string> report, CancellationToken ct)
    {
        await using var source = await media.OpenReadAsync(item);
        if (offset > 0)
            await SkipExactlyAsync(source, offset, ct);

        var sent = offset;
        var create = offset == 0;
        do
        {
            var chunkLength = Math.Min(ChunkSize, item.SizeBytes - sent);
            var baseSent = sent;
            var label = create ? "Subiendo…" : offset > 0 ? "Reanudando…" : "Subiendo…";
            using var content = new ChunkStreamContent(
                source, chunkLength, bytes => report(baseSent + bytes, label));

            if (create)
                await api.CreateFileAsync(remotePath, content, ct);
            else
                await api.AppendFileAsync(remotePath, content, ct);

            create = false;
            sent += chunkLength;
        } while (sent < item.SizeBytes);
    }

    private static async Task SkipExactlyAsync(Stream stream, long count, CancellationToken ct)
    {
        if (stream.CanSeek)
        {
            stream.Seek(count, SeekOrigin.Begin);
            return;
        }
        var buffer = new byte[81920];
        var remaining = count;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
            if (read == 0)
                throw new IOException("El archivo local es más corto de lo esperado.");
            remaining -= read;
        }
    }
}
