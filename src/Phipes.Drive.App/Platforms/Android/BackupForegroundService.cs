using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.Content;
using Phipes.Drive.App.Services;

namespace Phipes.Drive.App.Droid;

/// <summary>
/// Runs the backup as a dataSync foreground service so Android does not kill it halfway
/// through a large upload. It shows a persistent progress notification while it works and
/// a closing notification (summary, or a silent one on failure) when it stops.
/// </summary>
[Service(
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class BackupForegroundService : Service
{
    public const string ActionStart = "cl.phipes.drive.action.START_BACKUP";
    public const string ActionStop = "cl.phipes.drive.action.STOP_BACKUP";
    public const string ExtraRetryFailedOnly = "retry_failed_only";

    private static CancellationTokenSource? _cts;
    private static int _running;

    /// <summary>True while a backup pass is being driven by this service.</summary>
    public static bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>
    /// Starts the service. Throws on Android 12+ when the app is not allowed to start a
    /// foreground service from the background; callers fall back to an in-worker run.
    /// </summary>
    public static void Start(Context context, bool retryFailedOnly = false)
    {
        var intent = new Intent(context, typeof(BackupForegroundService));
        intent.SetAction(ActionStart);
        intent.PutExtra(ExtraRetryFailedOnly, retryFailedOnly);
        ContextCompat.StartForegroundService(context, intent);
    }

    /// <summary>Asks a running pass to stop; safe to call when nothing is running.</summary>
    public static void RequestStop() => _cts?.Cancel();

    private PowerManager.WakeLock? _wakeLock;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        BackupNotifications.EnsureChannels(this);

        if (intent?.Action == ActionStop)
        {
            RequestStop();
            if (!IsRunning)
                StopSelfSafely();
            return StartCommandResult.NotSticky;
        }

        // Android requires the notification within a few seconds of the start request, even
        // when we end up doing nothing. If it is refused, bail out instead of letting the
        // system kill us: the WorkManager fallback will run the pass itself.
        if (!StartForegroundCompat(BackupNotifications.BuildProgress(
                this, "Buscando archivos nuevos…", 0, 0, indeterminate: true)))
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        if (Interlocked.Exchange(ref _running, 1) == 1)
            return StartCommandResult.NotSticky;

        var retryFailedOnly = intent?.GetBooleanExtra(ExtraRetryFailedOnly, false) ?? false;
        _cts = new CancellationTokenSource();
        AcquireWakeLock();
        _ = RunAsync(retryFailedOnly, _cts.Token);
        return StartCommandResult.NotSticky;
    }

    private async Task RunAsync(bool retryFailedOnly, CancellationToken ct)
    {
        var lastUpdate = DateTime.MinValue;
        var progress = new Progress<SyncProgress>(p =>
        {
            // The shade throttles updates anyway; one per second keeps it readable.
            var now = DateTime.UtcNow;
            if (now - lastUpdate < TimeSpan.FromSeconds(1) && p.FileBytesSent < p.FileBytesTotal)
                return;
            lastUpdate = now;
            UpdateProgress($"Respaldando {p.FileIndex} de {p.FileCount}: {p.FileName}",
                p.FileIndex, p.FileCount);
        });

        BackupRunResult result;
        try
        {
            result = await BackupRunner.RunAsync(progress, ct, retryFailedOnly);
        }
        catch (Exception ex)
        {
            result = new BackupRunResult(BackupOutcome.Failed, null, ex.Message);
        }

        try
        {
            ReportResult(result);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
            _cts?.Dispose();
            _cts = null;
            ReleaseWakeLock();
            StopSelfSafely();
        }
    }

    private void ReportResult(BackupRunResult result)
    {
        switch (result.Outcome)
        {
            case BackupOutcome.Completed when result.Summary is not null:
                var summary = result.Summary;
                // Stay quiet when there was nothing to do: the point is unattended backup.
                if (summary.Uploaded + summary.Resumed > 0)
                {
                    BackupNotifications.ShowSummary(
                        this, "Respaldo completado", BackupRunner.Describe(summary));
                }
                if (summary.Errors > 0)
                {
                    var reason = summary.ErrorDetails.FirstOrDefault() ?? "Errores durante la subida.";
                    BackupNotifications.ShowError(this, $"{summary.Errors} con problemas. {reason}");
                }
                break;

            case BackupOutcome.SessionExpired:
                BackupNotifications.ShowError(
                    this, "La sesión expiró. Abre Phipes Drive e inicia sesión otra vez.");
                break;

            case BackupOutcome.Failed:
                BackupNotifications.ShowError(this, result.Message ?? "Error desconocido.");
                break;

            // Skipped and Cancelled are normal and silent.
        }
    }

    private void UpdateProgress(string text, int current, int total)
    {
        try
        {
            var manager = (NotificationManager?)GetSystemService(NotificationService);
            manager?.Notify(
                BackupNotifications.ProgressNotificationId,
                BackupNotifications.BuildProgress(this, text, current, total, indeterminate: false));
        }
        catch (Exception)
        {
            // Notification updates are cosmetic; never let them break a backup.
        }
    }

    private bool StartForegroundCompat(Notification notification)
    {
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
            {
                StartForeground(
                    BackupNotifications.ProgressNotificationId,
                    notification,
                    ForegroundService.TypeDataSync);
            }
            else
            {
                StartForeground(BackupNotifications.ProgressNotificationId, notification);
            }
            return true;
        }
        catch (Exception)
        {
            // Android 12+ can refuse a background start; the WorkManager fallback covers it.
            return false;
        }
    }

    private void StopSelfSafely()
    {
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(24))
                StopForeground(StopForegroundFlags.Remove);
            else
                BackupNotifications.CancelProgress(this);
            StopSelf();
        }
        catch (Exception)
        {
        }
    }

    private void AcquireWakeLock()
    {
        try
        {
            var power = (PowerManager?)GetSystemService(PowerService);
            _wakeLock = power?.NewWakeLock(WakeLockFlags.Partial, "PhipesDrive:backup");
            _wakeLock?.Acquire();
        }
        catch (Exception)
        {
        }
    }

    private void ReleaseWakeLock()
    {
        try
        {
            if (_wakeLock is { IsHeld: true })
                _wakeLock.Release();
        }
        catch (Exception)
        {
        }
        finally
        {
            _wakeLock?.Dispose();
            _wakeLock = null;
        }
    }
}
