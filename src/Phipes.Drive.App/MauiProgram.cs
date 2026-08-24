using Phipes.Drive.App.Pages;
using Phipes.Drive.App.Services;

namespace Phipes.Drive.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddSingleton<ApiClient>();
        builder.Services.AddSingleton<BackupSettings>();
        builder.Services.AddSingleton<BackupLedger>();
        builder.Services.AddSingleton<SyncService>();
        builder.Services.AddSingleton<RemotePhotoIndex>();
        builder.Services.AddSingleton<ThumbnailStore>();
        builder.Services.AddSingleton<ThumbnailLoader>();
        builder.Services.AddSingleton<OriginalStore>();
        builder.Services.AddSingleton<GalleryService>();
#if ANDROID
        builder.Services.AddSingleton<IMediaLibrary, Phipes.Drive.App.Droid.MediaLibrary>();
        builder.Services.AddSingleton<IMediaActions, Phipes.Drive.App.Droid.MediaActions>();
        builder.Services.AddSingleton<IBackgroundBackup, Phipes.Drive.App.Droid.BackgroundBackup>();
#else
        builder.Services.AddSingleton<IMediaLibrary, NullMediaLibrary>();
        builder.Services.AddSingleton<IMediaActions, NullMediaActions>();
        builder.Services.AddSingleton<IBackgroundBackup, NullBackgroundBackup>();
#endif

        builder.Services.AddSingleton<LoginPage>();
        builder.Services.AddSingleton<GalleryPage>();
        builder.Services.AddSingleton<BackupPage>();
        builder.Services.AddSingleton<SettingsPage>();
        builder.Services.AddTransient<ViewerPage>();

        return builder.Build();
    }
}
