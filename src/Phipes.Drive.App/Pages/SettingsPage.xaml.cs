using Phipes.Drive.App.Services;

namespace Phipes.Drive.App.Pages;

public partial class SettingsPage : ContentPage
{
    private readonly ApiClient _api;
    private readonly BackupSettings _settings;

    public SettingsPage(ApiClient api, BackupSettings settings)
    {
        InitializeComponent();
        _api = api;
        _settings = settings;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        AccountLabel.Text = _api.AccountEmail.Length > 0 ? _api.AccountEmail : "Sin sesión";
        ServerLabel.Text = _api.ServerUrl;
        WifiOnlySwitch.IsToggled = _settings.WifiOnly;
    }

    private void OnWifiOnlyToggled(object? sender, ToggledEventArgs e) =>
        _settings.WifiOnly = e.Value;

    private async void OnLogoutClicked(object? sender, EventArgs e)
    {
        var confirm = await DisplayAlert("Cerrar sesión",
            "¿Cerrar la sesión en este dispositivo? El respaldo automático dejará de funcionar hasta que vuelvas a iniciar sesión.",
            "Cerrar sesión", "Cancelar");
        if (!confirm)
            return;
        _api.Logout();
        await Shell.Current.GoToAsync("//login");
    }
}
