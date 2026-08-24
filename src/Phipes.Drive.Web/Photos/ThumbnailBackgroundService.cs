namespace Phipes.Drive.Web.Photos;

/// <summary>Drains the thumbnail queue filled by the indexer, one item at a time.</summary>
public sealed class ThumbnailBackgroundService(
    ThumbnailQueue queue,
    ThumbnailService thumbnails,
    ILogger<ThumbnailBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        try
        {
            await foreach (var hash in queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await thumbnails.GenerateAsync(hash, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning(e, "Queued thumbnail generation failed for {Hash}", hash);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
