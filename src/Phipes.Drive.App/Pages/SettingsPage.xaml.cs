using Phipes.Drive.App.Services;

namespace Phipes.Drive.App.Pages;

public partial class SettingsPage : ContentPage
{
    private readonly ApiClient _api;
    private readonly BackupSettings _settings;
    private readonly IBackgroundBackup _background;
    private readonly BackupLedger _ledger;
    private readonly RemotePhotoIndex _index;

    public SettingsPage(
        ApiClient api,
        BackupSettings settings,
        IBackgroundBackup background,
        BackupLedger ledger,
        RemotePhotoIndex index)
    {
        InitializeComponent();
        _api = api;
        _settings = settings;
        _background = background;
        _ledger = ledger;
        _index = index;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        AccountLabel.Text = _api.AccountEmail.Length > 0 ? _api.AccountEmail : "Sin sesión";
        ServerLabel.Text = _api.ServerUrl;
        WifiOnlySwitch.IsToggled = _settings.WifiOnly;
        await RefreshPermissionStatesAsync();
    }

    private async Task RefreshPermissionStatesAsync()
    {
        var battery = _background.IsBatteryOptimizationIgnored;
        BatteryStatusLabel.Text = battery
            ? "Concedido: el respaldo puede trabajar en segundo plano."
            : "Pendiente: Android puede pausar el respaldo.";
        BatteryStatusLabel.TextColor = battery
            ? Color.FromArgb("#2E7D32")
            : Color.FromArgb("#B26A00");
        BatteryButton.IsEnabled = !battery;
        BatteryButton.Text = battery
            ? "Ya está permitido"
            : "Permitir respaldo en segundo plano";

        var notifications = await _background.HasNotificationPermissionAsync();
        NotificationsStatusLabel.Text = notifications
            ? "Concedido: verás el avance y el resumen."
            : "Pendiente: el respaldo funciona igual, pero sin avisos.";
        NotificationsStatusLabel.TextColor = notifications
            ? Color.FromArgb("#2E7D32")
            : Color.FromArgb("#B26A00");
        NotificationsButton.IsEnabled = !notifications;
        NotificationsButton.Text = notifications ? "Ya están activas" : "Activar notificaciones";
    }

    private void OnWifiOnlyToggled(object? sender, ToggledEventArgs e) =>
        _settings.WifiOnly = e.Value;

    private async void OnBatteryOptimizationClicked(object? sender, EventArgs e)
    {
        await _background.RequestIgnoreBatteryOptimizationsAsync();
        // The system dialog runs outside the app; re-read the state when we come back.
        await Task.Delay(500);
        await RefreshPermissionStatesAsync();
    }

    private async void OnNotificationsClicked(object? sender, EventArgs e)
    {
        var granted = await _background.EnsureNotificationPermissionAsync();
        if (!granted)
        {
            await DisplayAlertAsync("Notificaciones",
                "Puedes activarlas en Ajustes de Android > Aplicaciones > Phipes Drive > Notificaciones.",
                "OK");
        }
        await RefreshPermissionStatesAsync();
    }

    private async void OnLogoutClicked(object? sender, EventArgs e)
    {
        var confirm = await DisplayAlertAsync("Cerrar sesión",
            "¿Cerrar la sesión en este dispositivo? El respaldo automático dejará de funcionar hasta que vuelvas a iniciar sesión.",
            "Cerrar sesión", "Cancelar");
        if (!confirm)
            return;

        _background.Stop();
        _background.CancelAll();
        _api.Logout();
        // The next account may be a different library: drop what we cached about this one.
        _ledger.Clear();
        _index.Clear();
        await Shell.Current.GoToAsync("//login");
    }
}
