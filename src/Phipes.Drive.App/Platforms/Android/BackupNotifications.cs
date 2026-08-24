using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;

namespace Phipes.Drive.App.Droid;

/// <summary>
/// Notification channels and builders for the background backup: a persistent low-priority
/// progress notification while it runs, a summary when it finishes with something to report,
/// and a silent one when it fails.
/// </summary>
public static class BackupNotifications
{
    public const string ProgressChannelId = "phipes_backup_progress";
    public const string SummaryChannelId = "phipes_backup_summary";
    public const string ErrorChannelId = "phipes_backup_error";

    public const int ProgressNotificationId = 4101;
    public const int SummaryNotificationId = 4102;
    public const int ErrorNotificationId = 4103;

    public static void EnsureChannels(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
            return;

        var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
        if (manager is null)
            return;

        // Low importance: visible in the shade, never makes a sound while backing up.
        var progress = new NotificationChannel(
            ProgressChannelId, "Respaldo en curso", NotificationImportance.Low)
        {
            Description = "Muestra el avance mientras se respaldan fotos y videos.",
        };
        progress.SetShowBadge(false);

        var summary = new NotificationChannel(
            SummaryChannelId, "Respaldo completado", NotificationImportance.Default)
        {
            Description = "Avisa cuando termina un respaldo con fotos nuevas.",
        };

        // Failures should not wake anybody up; they are informational.
        var error = new NotificationChannel(
            ErrorChannelId, "Problemas de respaldo", NotificationImportance.Low)
        {
            Description = "Avisa en silencio cuando un respaldo no se pudo completar.",
        };

        manager.CreateNotificationChannel(progress);
        manager.CreateNotificationChannel(summary);
        manager.CreateNotificationChannel(error);
    }

    public static Notification BuildProgress(
        Context context, string text, int current, int total, bool indeterminate)
    {
        var builder = new NotificationCompat.Builder(context, ProgressChannelId)
            .SetSmallIcon(Resource.Drawable.ic_notification)!
            .SetContentTitle("Respaldando fotos")!
            .SetContentText(text)!
            .SetOngoing(true)!
            .SetOnlyAlertOnce(true)!
            .SetSilent(true)!
            .SetCategory(NotificationCompat.CategoryService)!
            .SetPriority(NotificationCompat.PriorityLow)!
            .SetContentIntent(OpenAppIntent(context))!;

        if (indeterminate || total <= 0)
            builder.SetProgress(0, 0, true);
        else
            builder.SetProgress(total, current, false);

        builder.AddAction(
            Resource.Drawable.ic_notification,
            "Detener",
            StopIntent(context));

        return builder.Build()!;
    }

    public static void ShowSummary(Context context, string title, string text)
    {
        var notification = new NotificationCompat.Builder(context, SummaryChannelId)
            .SetSmallIcon(Resource.Drawable.ic_notification)!
            .SetContentTitle(title)!
            .SetContentText(text)!
            .SetStyle(new NotificationCompat.BigTextStyle().BigText(text))!
            .SetAutoCancel(true)!
            .SetContentIntent(OpenAppIntent(context))!
            .Build()!;
        Notify(context, SummaryNotificationId, notification);
    }

    public static void ShowError(Context context, string reason)
    {
        var notification = new NotificationCompat.Builder(context, ErrorChannelId)
            .SetSmallIcon(Resource.Drawable.ic_notification)!
            .SetContentTitle("No se pudo completar el respaldo")!
            .SetContentText(reason)!
            .SetStyle(new NotificationCompat.BigTextStyle().BigText(reason))!
            .SetAutoCancel(true)!
            .SetSilent(true)!
            .SetPriority(NotificationCompat.PriorityLow)!
            .SetContentIntent(OpenAppIntent(context))!
            .Build()!;
        Notify(context, ErrorNotificationId, notification);
    }

    public static void CancelProgress(Context context) =>
        NotificationManagerCompat.From(context)!.Cancel(ProgressNotificationId);

    private static void Notify(Context context, int id, Notification notification)
    {
        try
        {
            // Without POST_NOTIFICATIONS (Android 13+) this is a no-op, never a crash.
            NotificationManagerCompat.From(context)!.Notify(id, notification);
        }
        catch (Java.Lang.SecurityException)
        {
        }
    }

    private static PendingIntent? OpenAppIntent(Context context)
    {
        var intent = new Intent(context, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        return PendingIntent.GetActivity(
            context, 0, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
    }

    private static PendingIntent? StopIntent(Context context)
    {
        var intent = new Intent(context, typeof(BackupForegroundService));
        intent.SetAction(BackupForegroundService.ActionStop);
        return OperatingSystem.IsAndroidVersionAtLeast(26)
            ? PendingIntent.GetForegroundService(
                context, 1, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)
            : PendingIntent.GetService(
                context, 1, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
    }
}
