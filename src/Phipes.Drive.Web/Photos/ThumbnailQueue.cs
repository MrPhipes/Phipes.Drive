using System.Collections.Concurrent;
using System.Threading.Channels;
using Phipes.Drive.Core.Photos;

namespace Phipes.Drive.Web.Photos;

/// <summary>
/// Unbounded, de-duplicated work queue between the indexer and the thumbnail worker, so indexing
/// never waits on image decoding.
/// </summary>
public sealed class ThumbnailQueue : IThumbnailQueue
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly ConcurrentDictionary<string, byte> _pending = new();

    public void Enqueue(string hash)
    {
        if (!_pending.TryAdd(hash, 0)) return;
        if (!_channel.Writer.TryWrite(hash)) _pending.TryRemove(hash, out _);
    }

    public async IAsyncEnumerable<string> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var hash in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            _pending.TryRemove(hash, out _);
            yield return hash;
        }
    }
}
