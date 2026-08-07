using Android.Content;
using AndroidX.Work;
using Phipes.Drive.App.Services;

namespace Phipes.Drive.App.Droid;

/// <summary>
/// WorkManager worker that runs the same backup pass as the "Respaldar ahora" button.
/// Scheduled hourly by <see cref="BackupScheduler"/>; skips quietly when there is no
/// session, no allowed network, or a manual sync is already running.
/// </summary>
public class BackupWorker(Context context, WorkerParameters workerParams)
    : Worker(context, workerParams)
{
    public override Result DoWork()
    {
        var services = IPlatformApplication.Current?.Services;
        if (services is null)
            return Result.InvokeRetry()!;

        try
        {
            var api = services.GetRequiredService<ApiClient>();
            var sync = services.GetRequiredService<SyncService>();

            if (!api.HasSessionAsync().GetAwaiter().GetResult())
                return Result.InvokeSuccess()!;
            if (!sync.IsNetworkAllowed())
                return Result.InvokeSuccess()!;

            sync.RunAsync(null, CancellationToken.None).GetAwaiter().GetResult();
            return Result.InvokeSuccess()!;
        }
        catch (SessionExpiredException)
        {
            // Nothing the worker can do; the user must log in again from the app.
            return Result.InvokeSuccess()!;
        }
        catch
        {
            return Result.InvokeRetry()!;
        }
    }
}

/// <summary>Registers the periodic background backup with WorkManager.</summary>
public static class BackupScheduler
{
    private const string WorkName = "phipes-drive-backup";

    public static void Schedule(Context context)
    {
        var constraints = new Constraints.Builder()
            .SetRequiredNetworkType(NetworkType.Connected!)!
            .Build();

        var request = new PeriodicWorkRequest.Builder(
                typeof(BackupWorker), TimeSpan.FromHours(1))
            .SetConstraints(constraints)!
            .Build();

        WorkManager.GetInstance(context)!
            .EnqueueUniquePeriodicWork(WorkName, ExistingPeriodicWorkPolicy.Keep!, request);
    }
}
