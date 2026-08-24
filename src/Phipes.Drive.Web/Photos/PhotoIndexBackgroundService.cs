using Phipes.Drive.Core.Photos;

namespace Phipes.Drive.Web.Photos;

/// <summary>
/// Runs the incremental scan shortly after startup and then every
/// <see cref="PhotoOptions.ScanIntervalMinutes"/>, or as soon as someone posts to
/// <c>/api/photos/reindex</c>. Never blocks application start.
/// </summary>
public sealed class PhotoIndexBackgroundService(
    IServiceScopeFactory scopeFactory,
    PhotoScanTrigger trigger,
    PhotoOptions options,
    ILogger<PhotoIndexBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Photo indexing is disabled by configuration.");
            return;
        }

        // Yield first so the host finishes starting before the first (possibly long) scan.
        await Task.Yield();
        try { await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        var interval = TimeSpan.FromMinutes(Math.Max(1, options.ScanIntervalMinutes));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var indexer = scope.ServiceProvider.GetRequiredService<PhotoIndexer>();
                await indexer.ScanAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Photo scan failed; retrying at the next interval.");
            }

            await trigger.WaitAsync(interval, stoppingToken).ConfigureAwait(false);
        }
    }
}
