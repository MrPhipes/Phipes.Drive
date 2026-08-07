using Phipes.Drive.App.Services;

namespace Phipes.Drive.App.Pages;

public partial class MainPage : ContentPage
{
    private readonly SyncService _sync;
    private readonly IMediaLibrary _media;
    private readonly BackupSettings _settings;
    private CancellationTokenSource? _cts;

    public MainPage(SyncService sync, IMediaLibrary media, BackupSettings settings)
    {
        InitializeComponent();
        _sync = sync;
        _media = media;
        _settings = settings;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        RefreshFolders();
        RefreshLastRun();
    }

    private void RefreshFolders()
    {
        FoldersList.Children.Clear();
        foreach (var folder in _settings.Folders)
        {
            var grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                },
                ColumnSpacing = 8,
            };
            grid.Add(new Label
            {
                Text = folder,
                VerticalOptions = LayoutOptions.Center,
                LineBreakMode = LineBreakMode.MiddleTruncation,
            }, 0, 0);

            var remove = new Button
            {
                Text = "Quitar",
                FontSize = 12,
                Padding = new Thickness(10, 4),
                BackgroundColor = Colors.Transparent,
                TextColor = (Color)Application.Current!.Resources["Danger"],
            };
            remove.Clicked += (_, _) =>
            {
                _settings.RemoveFolder(folder);
                RefreshFolders();
            };
            grid.Add(remove, 1, 0);

            FoldersList.Children.Add(new Border
            {
                BackgroundColor = Colors.White,
                Stroke = Color.FromArgb("#DDE3EA"),
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
                Padding = new Thickness(12, 6),
                Content = grid,
            });
        }
    }

    private void RefreshLastRun()
    {
        var last = _settings.LastRunUtc;
        LastRunLabel.Text = last is null
            ? "Aún no se ha respaldado."
            : $"Fecha: {last.Value.ToLocalTime():dd-MM-yyyy HH:mm}";
        SummaryLabel.Text = _settings.LastResult;
    }

    private async void OnAddFolderClicked(object? sender, EventArgs e)
    {
        if (!await MediaPermissions.EnsureGrantedAsync())
        {
            await DisplayAlert("Permisos",
                "Se necesita permiso para leer fotos y videos.", "OK");
            return;
        }

        var current = _settings.Folders;
        var available = (await _media.GetAvailableFoldersAsync())
            .Where(f => !current.Contains(f, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (available.Length == 0)
        {
            await DisplayAlert("Carpetas", "No hay más carpetas con fotos o videos.", "OK");
            return;
        }

        var choice = await DisplayActionSheet("Agregar carpeta", "Cancelar", null, available);
        if (choice is not null && choice != "Cancelar")
        {
            _settings.AddFolder(choice);
            RefreshFolders();
        }
    }

    private async void OnBackupClicked(object? sender, EventArgs e)
    {
        if (_sync.IsRunning)
            return;

        if (!await MediaPermissions.EnsureGrantedAsync())
        {
            await DisplayAlert("Permisos",
                "Se necesita permiso para leer fotos y videos. Puedes otorgarlo en Ajustes de Android.",
                "OK");
            return;
        }

        if (!_sync.IsNetworkAllowed())
        {
            await DisplayAlert("Sin red permitida",
                _settings.WifiOnly
                    ? "El respaldo está limitado a WiFi y no hay una red WiFi disponible. Puedes cambiarlo en Ajustes."
                    : "No hay conexión a internet.",
                "OK");
            return;
        }

        _cts = new CancellationTokenSource();
        BackupButton.IsEnabled = false;
        CancelButton.IsVisible = true;
        ProgressPanel.IsVisible = true;
        ErrorsLabel.IsVisible = false;
        ProgressFileLabel.Text = "Buscando archivos…";
        ProgressBytesLabel.Text = string.Empty;
        FileProgressBar.Progress = 0;

        var progress = new Progress<SyncProgress>(p =>
        {
            ProgressFileLabel.Text = $"({p.FileIndex}/{p.FileCount}) {p.FileName}";
            FileProgressBar.Progress = p.FileBytesTotal > 0
                ? (double)p.FileBytesSent / p.FileBytesTotal
                : 0;
            ProgressBytesLabel.Text =
                $"{p.Status}  {FormatBytes(p.FileBytesSent)} / {FormatBytes(p.FileBytesTotal)}";
        });

        try
        {
            var summary = await _sync.RunAsync(progress, _cts.Token);
            RefreshLastRun();
            if (summary is null)
            {
                await DisplayAlert("Respaldo", "Ya hay un respaldo en curso.", "OK");
            }
            else
            {
                if (summary.Errors > 0)
                {
                    ErrorsLabel.Text = string.Join("\n", summary.ErrorDetails.Take(10));
                    ErrorsLabel.IsVisible = true;
                }
                await DisplayAlert("Respaldo terminado", summary.ToString(), "OK");
            }
        }
        catch (OperationCanceledException)
        {
            RefreshLastRun();
            await DisplayAlert("Respaldo", "Respaldo cancelado.", "OK");
        }
        catch (SessionExpiredException)
        {
            await DisplayAlert("Sesión", "La sesión expiró. Inicia sesión nuevamente.", "OK");
            await Shell.Current.GoToAsync("//login");
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", ex.Message, "OK");
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            BackupButton.IsEnabled = true;
            CancelButton.IsVisible = false;
            ProgressPanel.IsVisible = false;
        }
    }

    private void OnCancelClicked(object? sender, EventArgs e) => _cts?.Cancel();

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.0} KB",
        _ => $"{bytes} B",
    };
}
