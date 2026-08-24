using Android.Content;
using AndroidX.Work;
using Phipes.Drive.App.Services;

namespace Phipes.Drive.App.Droid;

/// <summary>
/// WorkManager entry point for the unattended backup. Its job is to hand the work over to
/// <see cref="BackupForegroundService"/>; when Android refuses a background foreground-service
/// start (API 31+ outside the battery allowlist) the worker promotes itself instead and runs
/// the same pass with the same notification.
/// </summary>
public class BackupWorker(Context context, WorkerParameters workerParams)
    : Worker(context, workerParams)
{
    public override Result DoWork()
    {
        BackupNotifications.EnsureChannels(ApplicationContext!);

        if (BackupForegroundService.IsRunning)
            return Result.InvokeSuccess()!;

        // Cheap preflight so an idle device does not light up a notification for nothing.
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
        }
        catch (SessionExpiredException)
        {
            return Result.InvokeSuccess()!;
        }
        catch
        {
            return Result.InvokeRetry()!;
        }

        try
        {
            BackupForegroundService.Start(ApplicationContext!);
            // Give the service a moment to take over. If Android refused the foreground start,
            // it never gets going, and the pass has to happen inside the worker instead.
            Thread.Sleep(TimeSpan.FromSeconds(2));
            if (BackupForegroundService.IsRunning)
                return Result.InvokeSuccess()!;
            return RunInline();
        }
        catch (Exception)
        {
            // ForegroundServiceStartNotAllowedException and friends: fall back to running here.
            return RunInline();
        }
    }

    /// <summary>
    /// Runs the pass inside the worker, promoted to a foreground worker. WorkManager owns the
    /// foreground-service start in this path, so it is allowed even from the background.
    /// </summary>
    private Result RunInline()
    {
        var lastUpdate = DateTime.MinValue;
        var progress = new Progress<SyncProgress>(p =>
        {
            var now = DateTime.UtcNow;
            if (now - lastUpdate < TimeSpan.FromSeconds(1))
                return;
            lastUpdate = now;
            SetForegroundSafely($"Respaldando {p.FileIndex} de {p.FileCount}: {p.FileName}",
                p.FileIndex, p.FileCount);
        });

        SetForegroundSafely("Buscando archivos nuevos…", 0, 0);

        var result = BackupRunner.RunAsync(progress, CancellationToken.None)
            .GetAwaiter().GetResult();

        switch (result.Outcome)
        {
            case BackupOutcome.Completed when result.Summary is not null:
                if (result.Summary.Uploaded + result.Summary.Resumed > 0)
                {
                    BackupNotifications.ShowSummary(
                        ApplicationContext!, "Respaldo completado",
                        BackupRunner.Describe(result.Summary));
                }
                if (result.Summary.Errors > 0)
                {
                    BackupNotifications.ShowError(
                        ApplicationContext!,
                        result.Summary.ErrorDetails.FirstOrDefault() ?? "Errores durante la subida.");
                }
                return Result.InvokeSuccess()!;

            case BackupOutcome.SessionExpired:
                BackupNotifications.ShowError(
                    ApplicationContext!,
                    "La sesión expiró. Abre Phipes Drive e inicia sesión otra vez.");
                return Result.InvokeSuccess()!;

            case BackupOutcome.Failed:
                BackupNotifications.ShowError(
                    ApplicationContext!, result.Message ?? "Error desconocido.");
                return Result.InvokeRetry()!;

            default:
                return Result.InvokeSuccess()!;
        }
    }

    private void SetForegroundSafely(string text, int current, int total)
    {
        try
        {
            var notification = BackupNotifications.BuildProgress(
                ApplicationContext!, text, current, total, indeterminate: total <= 0);
            var info = OperatingSystem.IsAndroidVersionAtLeast(29)
                ? new ForegroundInfo(
                    BackupNotifications.ProgressNotificationId, notification,
                    (int)global::Android.Content.PM.ForegroundService.TypeDataSync)
                : new ForegroundInfo(BackupNotifications.ProgressNotificationId, notification);
            SetForegroundAsync(info);
        }
        catch (Exception)
        {
            // Older devices or missing notification permission: the pass still runs.
        }
    }
}

/// <summary>
/// Registers the background triggers with WorkManager:
/// a periodic hourly pass, and an opportunistic one that fires when the phone lands on an
/// unmetered network while charging (typically: plugged in at home overnight).
/// </summary>
public static class BackupScheduler
{
    private const string PeriodicWorkName = "phipes-drive-backup";
    private const string OpportunisticWorkName = "phipes-drive-backup-opportunistic";
    private const string ImmediateWorkName = "phipes-drive-backup-now";

    public static void Schedule(Context context)
    {
        var manager = WorkManager.GetInstance(context)!;

        var connected = new Constraints.Builder()
            .SetRequiredNetworkType(NetworkType.Connected!)!
            .Build();

        var periodic = new PeriodicWorkRequest.Builder(typeof(BackupWorker), TimeSpan.FromHours(1))
            .SetConstraints(connected)!
            .Build();
        manager.EnqueueUniquePeriodicWork(
            PeriodicWorkName, ExistingPeriodicWorkPolicy.Keep!, periodic);

        // WorkManager runs this as soon as the constraints turn true inside each window, so in
        // practice it is "backup when you plug in on WiFi" without a broadcast receiver.
        var wifiAndCharging = new Constraints.Builder()
            .SetRequiredNetworkType(NetworkType.Unmetered!)!
            .SetRequiresCharging(true)!
            .Build();

        var opportunistic = new PeriodicWorkRequest.Builder(
                typeof(BackupWorker), TimeSpan.FromMinutes(20))
            .SetConstraints(wifiAndCharging)!
            .Build();
        manager.EnqueueUniquePeriodicWork(
            OpportunisticWorkName, ExistingPeriodicWorkPolicy.Keep!, opportunistic);
    }

    /// <summary>Queues one pass to start as soon as there is a network (used by "Respaldar ahora").</summary>
    public static void RunOnce(Context context)
    {
        var constraints = new Constraints.Builder()
            .SetRequiredNetworkType(NetworkType.Connected!)!
            .Build();
        var request = new OneTimeWorkRequest.Builder(typeof(BackupWorker))
            .SetConstraints(constraints)!
            .Build();
        WorkManager.GetInstance(context)!.EnqueueUniqueWork(
            ImmediateWorkName, ExistingWorkPolicy.Replace!, request);
    }

    /// <summary>Drops every registered trigger (used on logout).</summary>
    public static void CancelAll(Context context)
    {
        var manager = WorkManager.GetInstance(context)!;
        manager.CancelUniqueWork(PeriodicWorkName);
        manager.CancelUniqueWork(OpportunisticWorkName);
        manager.CancelUniqueWork(ImmediateWorkName);
    }
}
