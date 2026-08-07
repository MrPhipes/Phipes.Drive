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
        builder.Services.AddSingleton<SyncService>();
#if ANDROID
        builder.Services.AddSingleton<IMediaLibrary, Phipes.Drive.App.Droid.MediaLibrary>();
#else
        builder.Services.AddSingleton<IMediaLibrary, NullMediaLibrary>();
#endif

        builder.Services.AddSingleton<LoginPage>();
        builder.Services.AddSingleton<MainPage>();
        builder.Services.AddSingleton<SettingsPage>();

        return builder.Build();
    }
}
