using Android.Content;
using Android.OS;
using Android.Provider;
using Phipes.Drive.App.Services;
using AndroidUri = Android.Net.Uri;
using Application = Android.App.Application;

namespace Phipes.Drive.App.Droid;

/// <summary>Android implementation of the unattended backup controls.</summary>
public sealed class BackgroundBackup : IBackgroundBackup
{
    private static Context Ctx => Application.Context;

    public bool IsSupported => true;

    public bool IsRunning => BackupForegroundService.IsRunning;

    public void Schedule() => BackupScheduler.Schedule(Ctx);

    public void StartNow(bool retryFailedOnly = false)
    {
        try
        {
            BackupForegroundService.Start(Ctx, retryFailedOnly);
        }
        catch (Exception)
        {
            // Not allowed to start a service right now: let WorkManager do it.
            BackupScheduler.RunOnce(Ctx);
        }
    }

    public void Stop() => BackupForegroundService.RequestStop();

    public void CancelAll() => BackupScheduler.CancelAll(Ctx);

    public bool IsBatteryOptimizationIgnored
    {
        get
        {
            try
            {
                var power = (PowerManager?)Ctx.GetSystemService(Context.PowerService);
                return power?.IsIgnoringBatteryOptimizations(Ctx.PackageName!) ?? false;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public Task RequestIgnoreBatteryOptimizationsAsync()
    {
        try
        {
            // Takes the user straight to the "allow background activity" system dialog.
            var intent = new Intent(Settings.ActionRequestIgnoreBatteryOptimizations);
            intent.SetData(AndroidUri.Parse("package:" + Ctx.PackageName));
            var activity = Platform.CurrentActivity;
            if (activity is not null)
            {
                activity.StartActivity(intent);
            }
            else
            {
                intent.AddFlags(ActivityFlags.NewTask);
                Ctx.StartActivity(intent);
            }
        }
        catch (Exception)
        {
            // Some ROMs hide the dialog; fall back to the app's battery settings screen.
            try
            {
                var fallback = new Intent(Settings.ActionIgnoreBatteryOptimizationSettings);
                fallback.AddFlags(ActivityFlags.NewTask);
                Ctx.StartActivity(fallback);
            }
            catch (Exception)
            {
            }
        }
        return Task.CompletedTask;
    }

    public async Task<bool> EnsureNotificationPermissionAsync()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33))
            return true;
        var status = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();
        if (status != PermissionStatus.Granted)
            status = await Permissions.RequestAsync<Permissions.PostNotifications>();
        return status == PermissionStatus.Granted;
    }

    public async Task<bool> HasNotificationPermissionAsync()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33))
            return true;
        return await Permissions.CheckStatusAsync<Permissions.PostNotifications>() ==
               PermissionStatus.Granted;
    }
}
