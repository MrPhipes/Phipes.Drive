using System.Collections.Concurrent;
using Phipes.Drive.App.ViewModels;

namespace Phipes.Drive.App.Services;

/// <summary>
/// Feeds thumbnails to the timeline tiles. Requests are served newest-first (what the user
/// just scrolled to matters more than what they scrolled past) by a small pool of workers,
/// and the number of decoded thumbnails held in memory is capped so a library with tens of
/// thousands of items cannot exhaust the heap.
/// </summary>
public sealed class ThumbnailLoader(ThumbnailStore store)
{
    /// <summary>Grid tile size in pixels; the server exposes 256 and 1024.</summary>
    public const int TileSize = 256;

    /// <summary>How many decoded thumbnails may stay alive. A screen holds well under 40.</summary>
    private const int MaxLoaded = 240;

    private const int Workers = 3;

    private readonly ConcurrentStack<GalleryItemVm> _pending = new();
    private readonly LinkedList<GalleryItemVm> _loaded = new();
    private readonly Dictionary<GalleryItemVm, LinkedListNode<GalleryItemVm>> _nodes = [];
    private readonly object _sync = new();
    private int _active;
    private CancellationTokenSource _cts = new();

    /// <summary>Queues the thumbnail for a tile that just became visible.</summary>
    public void Request(GalleryItemVm vm)
    {
        _pending.Push(vm);
        PumpWorkers();
    }

    /// <summary>Marks a tile as recently used so it is evicted last.</summary>
    public void Touch(GalleryItemVm vm)
    {
        lock (_sync)
        {
            if (!_nodes.TryGetValue(vm, out var node))
                return;
            _loaded.Remove(node);
            _loaded.AddLast(node);
        }
    }

    /// <summary>Drops every pending request; used when the timeline is rebuilt.</summary>
    public void Reset()
    {
        CancellationTokenSource old;
        lock (_sync)
        {
            _pending.Clear();
            _loaded.Clear();
            _nodes.Clear();
            old = _cts;
            _cts = new CancellationTokenSource();
        }
        // Not disposed on purpose: workers still hold this token, and cancelling a disposed
        // source turns every in-flight request into an ObjectDisposedException.
        old.Cancel();
    }

    private void PumpWorkers()
    {
        while (true)
        {
            var active = Volatile.Read(ref _active);
            if (active >= Workers)
                return;
            if (Interlocked.CompareExchange(ref _active, active + 1, active) == active)
                break;
        }
        _ = Task.Run(WorkAsync);
    }

    private async Task WorkAsync()
    {
        try
        {
            CancellationToken ct;
            lock (_sync)
                ct = _cts.Token;

            while (!ct.IsCancellationRequested && _pending.TryPop(out var vm))
            {
                byte[]? bytes;
                try
                {
                    bytes = await store.GetAsync(vm.Item, TileSize, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    continue;
                }

                if (bytes is null || ct.IsCancellationRequested)
                    continue;

                var evict = Register(vm);
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    vm.ApplyThumbnail(bytes);
                    foreach (var stale in evict)
                        stale.DropThumbnail();
                });
            }
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            if (!_pending.IsEmpty)
                PumpWorkers();
        }
    }

    private List<GalleryItemVm> Register(GalleryItemVm vm)
    {
        var evict = new List<GalleryItemVm>();
        lock (_sync)
        {
            if (_nodes.TryGetValue(vm, out var existing))
            {
                _loaded.Remove(existing);
                _loaded.AddLast(existing);
            }
            else
            {
                _nodes[vm] = _loaded.AddLast(vm);
            }

            while (_loaded.Count > MaxLoaded)
            {
                var oldest = _loaded.First;
                if (oldest is null)
                    break;
                _loaded.RemoveFirst();
                _nodes.Remove(oldest.Value);
                evict.Add(oldest.Value);
            }
        }
        return evict;
    }
}
