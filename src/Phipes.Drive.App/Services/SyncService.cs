using System.Net.Sockets;

namespace Phipes.Drive.App.Services;

public sealed record SyncProgress(
    int FileIndex,
    int FileCount,
    string FileName,
    long FileBytesSent,
    long FileBytesTotal,
    string Status);

/// <summary>One item that did not make it through a pass.</summary>
/// <param name="Key">name|size key, so the retry pass can find it again.</param>
/// <param name="Transient">True when it is worth trying again by itself (network, 5xx, timeout).</param>
public sealed record SyncFailure(string Key, string Name, string Message, bool Transient);

public sealed record SyncSummary(
    int Uploaded,
    int Resumed,
    int Skipped,
    int Errors,
    IReadOnlyList<string> ErrorDetails,
    int TransientErrors = 0,
    int PermanentErrors = 0)
{
    public override string ToString()
    {
        var text = $"Subidos {Uploaded} · Reanudados {Resumed} · Saltados {Skipped} · Errores {Errors}";
        if (TransientErrors > 0)
            text += $" ({TransientErrors} reintentables)";
        return text;
    }
}

/// <summary>
/// Backs up device photos and videos to the server.
///
/// For each item: ask the server how much of it is already there, skip when the sizes match,
/// otherwise stream the rest in chunks (the first chunk creates the file, the rest append).
/// Mobile networks drop constantly -- Android cuts the app sockets when the screen goes off --
/// so every chunk is retried with exponential backoff, and every retry re-reads the real
/// remote size first so the upload resumes exactly where the server stopped receiving.
/// Whatever still fails is retried once more at the end of the pass and, if it still fails,
/// remembered so the user can retry just those.
/// </summary>
public sealed class SyncService(
    ApiClient api, IMediaLibrary media, BackupSettings settings, BackupLedger ledger)
{
    /// <summary>
    /// Chunk size per request. Small enough that losing one costs little on a flaky link,
    /// and well under the per-request limit of the server edge.
    /// </summary>
    public const long ChunkSize = 16L * 1024 * 1024;

    /// <summary>Backoff between attempts on the same file: four tries in total.</summary>
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    ];

    private int _running;

    /// <summary>True while a sync (manual or background) is in progress.</summary>
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>
    /// Raised on a background thread for every progress tick, whoever started the pass.
    /// Lets the backup screen mirror a run that the foreground service is driving.
    /// </summary>
    public event Action<SyncProgress>? Progressed;

    /// <summary>Raised on a background thread when a pass ends (null when it was cancelled).</summary>
    public event Action<SyncSummary?>? Finished;

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
    /// Runs a backup pass. Returns null when another pass is already running.
    /// </summary>
    /// <param name="onlyFailed">Only retry the items remembered from the last pass.</param>
    /// <exception cref="SessionExpiredException">The stored session is no longer valid.</exception>
    public async Task<SyncSummary?> RunAsync(
        IProgress<SyncProgress>? progress, CancellationToken ct, bool onlyFailed = false)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
            return null;
        try
        {
            var summary = await RunCoreAsync(progress, ct, onlyFailed);
            settings.LastRunUtc = DateTimeOffset.UtcNow;
            settings.LastResult = summary.ToString();
            Finished?.Invoke(summary);
            return summary;
        }
        catch (OperationCanceledException)
        {
            Finished?.Invoke(null);
            throw;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<SyncSummary> RunCoreAsync(
        IProgress<SyncProgress>? progress, CancellationToken ct, bool onlyFailed)
    {
        var items = await media.GetItemsAsync(settings.Folders.ToList());
        if (onlyFailed)
        {
            var wanted = settings.FailedKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            items = items
                .Where(i => wanted.Contains(BackupLedger.KeyFor(i.Name, i.SizeBytes)))
                .ToList();
        }

        var state = new PassState();
        await ProcessAsync(items, state, progress, ct);

        // Second pass over whatever failed for a reason that may have gone away by now.
        if (state.Retryable.Count > 0 && !state.Stopped && !ct.IsCancellationRequested)
        {
            var again = state.Retryable.ToList();
            state.Retryable.Clear();
            state.Failures.RemoveAll(f => f.Transient);
            await ProcessAsync(again, state, progress, ct);
        }

        settings.FailedKeys = state.Failures.Select(f => f.Key).Distinct().ToList();

        var transient = state.Failures.Count(f => f.Transient);
        return new SyncSummary(
            state.Uploaded,
            state.Resumed,
            state.Skipped,
            state.Failures.Count,
            state.Failures.Select(f => $"{f.Name}: {f.Message}").ToList(),
            transient,
            state.Failures.Count - transient);
    }

    private async Task ProcessAsync(
        IReadOnlyList<MediaItem> items,
        PassState state,
        IProgress<SyncProgress>? progress,
        CancellationToken ct)
    {
        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[i];
            var key = BackupLedger.KeyFor(item.Name, item.SizeBytes);
            var remotePath = RemotePathFor(item);

            void Report(long sent, string status)
            {
                var tick = new SyncProgress(
                    i + 1, items.Count, item.Name, sent, item.SizeBytes, status);
                progress?.Report(tick);
                Progressed?.Invoke(tick);
            }

            // Already known to be on the server: skip without a round trip. This is what keeps
            // frequent background passes cheap once the library is backed up.
            if (ledger.Contains(key))
            {
                state.Skipped++;
                Report(item.SizeBytes, "Ya respaldado");
                continue;
            }

            try
            {
                await TransferAsync(item, key, remotePath, state, Report, ct);
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
                state.Failures.Add(new SyncFailure(key, item.Name, ex.Message, Transient: false));
                state.Failures.Add(new SyncFailure(
                    key, "Respaldo detenido", "la cuota del servidor está llena.", Transient: false));
                state.Stopped = true;
                break;
            }
            catch (Exception ex)
            {
                var transient = IsTransient(ex);
                state.Failures.Add(new SyncFailure(key, item.Name, ex.Message, transient));
                if (transient)
                    state.Retryable.Add(item);
            }
        }
    }

    /// <summary>
    /// Moves one item to the server, retrying with backoff. Each attempt starts by asking the
    /// server how many bytes it actually has, so an upload interrupted by a dropped socket
    /// resumes from the real offset instead of starting over.
    /// </summary>
    private async Task TransferAsync(
        MediaItem item,
        string key,
        string remotePath,
        PassState state,
        Action<long, string> report,
        CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                report(0, "Consultando…");
                var stat = await api.StatAsync(remotePath, ct);

                if (stat.Exists && stat.IsFolder == true)
                    throw new ApiException("la ruta remota es una carpeta.");
                if (stat.Exists && stat.SizeBytes > item.SizeBytes)
                    throw new ApiException("el remoto es más grande que el local; se omitió.");

                var offset = stat.Exists ? stat.SizeBytes ?? 0 : 0;
                if (offset == item.SizeBytes)
                {
                    state.Skipped++;
                    await ledger.AddAsync(key);
                    report(item.SizeBytes, "Ya respaldado");
                    return;
                }

                await UploadFromAsync(item, remotePath, offset, report, ct);
                if (offset > 0)
                    state.Resumed++;
                else
                    state.Uploaded++;
                await ledger.AddAsync(key);
                report(item.SizeBytes, "Completado");
                return;
            }
            catch (Exception ex)
                when (IsTransient(ex) && attempt < RetryDelays.Length && !ct.IsCancellationRequested)
            {
                var delay = RetryDelays[attempt];
                attempt++;
                report(0, $"Reintento {attempt} en {delay.TotalSeconds:0}s…");
                await Task.Delay(delay, ct);
                // Loop: the next attempt re-reads the remote size and resumes from there.
            }
        }
    }

    /// <summary>Remote layout: Camera/&lt;yyyy&gt;/&lt;MM&gt;/&lt;name&gt; by capture date.</summary>
    public static string RemotePathFor(MediaItem item) =>
        $"Camera/{item.TakenLocal:yyyy}/{item.TakenLocal:MM}/{item.Name}";

    /// <summary>
    /// A failure worth another attempt: the socket died, the request timed out, or the server
    /// answered 5xx/408/429. Everything else is the caller being wrong and will not improve.
    /// </summary>
    public static bool IsTransient(Exception? ex)
    {
        while (ex is not null)
        {
            switch (ex)
            {
                case TransientApiException:
                case SocketException:
                case HttpRequestException:
                case TimeoutException:
                case IOException:
                    return true;
                case SessionExpiredException:
                case QuotaExceededException:
                case RemoteConflictException:
                    return false;
            }
            ex = ex.InnerException;
        }
        return false;
    }

    private async Task UploadFromAsync(
        MediaItem item,
        string remotePath,
        long offset,
        Action<long, string> report,
        CancellationToken ct)
    {
        if (offset >= item.SizeBytes)
            return;

        await using var source = await media.OpenReadAsync(item);
        if (offset > 0)
            await SkipExactlyAsync(source, offset, ct);

        var sent = offset;
        var create = offset == 0;
        var label = create ? "Subiendo…" : "Reanudando…";
        do
        {
            ct.ThrowIfCancellationRequested();
            var chunkLength = Math.Min(ChunkSize, item.SizeBytes - sent);
            var baseSent = sent;
            using var content = new ChunkStreamContent(
                source, chunkLength, bytes => report(baseSent + bytes, label));

            if (create)
                await api.CreateFileAsync(remotePath, content, ct);
            else
                await api.AppendFileAsync(remotePath, content, ct);

            create = false;
            label = "Subiendo…";
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

    /// <summary>Counters and leftovers carried across the two passes of one run.</summary>
    private sealed class PassState
    {
        public int Uploaded;
        public int Resumed;
        public int Skipped;
        public bool Stopped;
        public List<SyncFailure> Failures { get; } = [];
        public List<MediaItem> Retryable { get; } = [];
    }
}
