using Android.App;
using Android.Content.PM;
using Android.OS;

namespace Phipes.Drive.App.Droid;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation |
                           ConfigChanges.UiMode | ConfigChanges.ScreenLayout |
                           ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        // Notification channels must exist before the first backup posts anything.
        BackupNotifications.EnsureChannels(this);
        // Register (or keep) the background backup triggers.
        BackupScheduler.Schedule(this);
    }
}
