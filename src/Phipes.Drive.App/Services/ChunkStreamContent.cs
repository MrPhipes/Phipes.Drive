using System.Net;
using System.Net.Http.Headers;

namespace Phipes.Drive.App.Services;

/// <summary>
/// Streams up to <c>length</c> bytes from a source stream as an HTTP request body,
/// reporting progress, without buffering the whole chunk in memory.
/// </summary>
public sealed class ChunkStreamContent : HttpContent
{
    private readonly Stream _source;
    private readonly long _length;
    private readonly Action<long>? _onProgress;

    public ChunkStreamContent(Stream source, long length, Action<long>? onProgress = null)
    {
        _source = source;
        _length = length;
        _onProgress = onProgress;
        Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        var buffer = new byte[81920];
        var remaining = _length;
        long sent = 0;
        while (remaining > 0)
        {
            var read = await _source.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
            if (read == 0)
                throw new IOException("El archivo local terminó antes de lo esperado.");
            await stream.WriteAsync(buffer.AsMemory(0, read));
            remaining -= read;
            sent += read;
            _onProgress?.Invoke(sent);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _length;
        return true;
    }
}
