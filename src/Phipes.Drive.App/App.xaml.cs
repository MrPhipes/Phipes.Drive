using Phipes.Drive.App.Services;

namespace Phipes.Drive.App;

public partial class App : Application
{
    private readonly ApiClient _api;

    public App(ApiClient api)
    {
        InitializeComponent();
        _api = api;
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
    }
}
