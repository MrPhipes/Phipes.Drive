using Phipes.Drive.App.Services;

namespace Phipes.Drive.App;

public partial class App : Application
{
    private const string NotificationsAskedKey = "app.notifications_asked";

    private readonly ApiClient _api;
    private readonly IBackgroundBackup _background;

    public App(ApiClient api, IBackgroundBackup background)
    {
        InitializeComponent();
        _api = api;
        _background = background;
    }

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new AppShell());

    protected override async void OnStart()
    {
        base.OnStart();
        bool hasSession;
        try
        {
            hasSession = await _api.HasSessionAsync();
        }
        catch
        {
            hasSession = false;
        }

        await Shell.Current.GoToAsync(hasSession ? "//main" : "//login");

        // First launch: ask for notifications so the backup can report what it is doing.
        if (!Preferences.Default.Get(NotificationsAskedKey, false))
        {
            Preferences.Default.Set(NotificationsAskedKey, true);
            try
            {
                await _background.EnsureNotificationPermissionAsync();
            }
            catch
            {
                // Declining is fine: the backup still runs, just quietly.
            }
        }

        if (hasSession)
            _background.Schedule();
    }
}
