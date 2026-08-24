namespace Phipes.Drive.Web.Photos;

/// <summary>Lets <c>POST /api/photos/reindex</c> wake the background scanner between intervals.</summary>
public sealed class PhotoScanTrigger
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void RequestScan()
    {
        try { _signal.Release(); }
        catch (SemaphoreFullException) { /* a scan is already pending */ }
    }

    /// <summary>Waits for a manual trigger, or for the interval to elapse.</summary>
    public async Task WaitAsync(TimeSpan interval, CancellationToken ct)
    {
        try { await _signal.WaitAsync(interval, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
