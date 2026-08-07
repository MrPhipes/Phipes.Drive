using Phipes.Drive.App.Services;

namespace Phipes.Drive.App.Pages;

public partial class LoginPage : ContentPage
{
    private readonly ApiClient _api;

    public LoginPage(ApiClient api)
    {
        InitializeComponent();
        _api = api;
        ServerEntry.Text = _api.ServerUrl;
        EmailEntry.Text = _api.AccountEmail;
    }

    private async void OnLoginClicked(object? sender, EventArgs e)
    {
        var email = EmailEntry.Text?.Trim() ?? string.Empty;
        var password = PasswordEntry.Text ?? string.Empty;
        if (email.Length == 0 || password.Length == 0)
        {
            ShowError("Ingresa tu correo y contraseña.");
            return;
        }

        ErrorLabel.IsVisible = false;
        LoginButton.IsEnabled = false;
        Busy.IsVisible = Busy.IsRunning = true;
        try
        {
            var server = string.IsNullOrWhiteSpace(ServerEntry.Text)
                ? ApiClient.DefaultServerUrl
                : ServerEntry.Text;
            await _api.LoginAsync(server, email, password);
            PasswordEntry.Text = string.Empty;
            await Shell.Current.GoToAsync("//main");
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
        }
        catch (Exception)
        {
            ShowError("No se pudo conectar con el servidor. Revisa la URL y tu conexión.");
        }
        finally
        {
            LoginButton.IsEnabled = true;
            Busy.IsVisible = Busy.IsRunning = false;
        }
    }

    private void ShowError(string message)
    {
        ErrorLabel.Text = message;
        ErrorLabel.IsVisible = true;
    }
}
