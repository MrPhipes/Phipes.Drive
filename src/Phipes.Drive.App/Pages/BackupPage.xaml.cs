using Phipes.Drive.App.Services;

namespace Phipes.Drive.App.Pages;

/// <summary>
/// Backup screen. It no longer runs the upload itself: "Respaldar ahora" hands the pass to
/// the foreground service so it survives leaving the app, and this page just mirrors the
/// progress that the service reports.
/// </summary>
public partial class BackupPage : ContentPage
{
    private readonly SyncService _sync;
    private readonly IMediaLibrary _media;
    private readonly BackupSettings _settings;
    private readonly IBackgroundBackup _background;

    public BackupPage(
        SyncService sync, IMediaLibrary media, BackupSettings settings, IBackgroundBackup background)
    {
        InitializeComponent();
        _sync = sync;
        _media = media;
        _settings = settings;
        _background = background;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        RefreshFolders();
        RefreshLastRun();
        _sync.Progressed += OnProgressed;
        _sync.Finished += OnFinished;
        ApplyRunningState(_sync.IsRunning);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _sync.Progressed -= OnProgressed;
        _sync.Finished -= OnFinished;
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
        RefreshRetryButton();
    }

    private void RefreshRetryButton()
    {
        var pending = _settings.FailedKeys.Count;
        RetryFailedButton.IsVisible = pending > 0 && !_sync.IsRunning;
        RetryFailedButton.Text = pending == 1
            ? "Reintentar 1 archivo con error"
            : $"Reintentar {pending} archivos con error";
    }

    private async void OnAddFolderClicked(object? sender, EventArgs e)
    {
        if (!await MediaPermissions.EnsureGrantedAsync())
        {
            await DisplayAlertAsync("Permisos",
                "Se necesita permiso para leer fotos y videos.", "OK");
            return;
        }

        var current = _settings.Folders;
        var available = (await _media.GetAvailableFoldersAsync())
            .Where(f => !current.Contains(f, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (available.Length == 0)
        {
            await DisplayAlertAsync("Carpetas", "No hay más carpetas con fotos o videos.", "OK");
            return;
        }

        var choice = await DisplayActionSheetAsync("Agregar carpeta", "Cancelar", null, available);
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
            await DisplayAlertAsync("Permisos",
                "Se necesita permiso para leer fotos y videos. Puedes otorgarlo en Ajustes de Android.",
                "OK");
            return;
        }

        // Without this the progress notification is invisible, but the backup still runs.
        await _background.EnsureNotificationPermissionAsync();

        if (!_sync.IsNetworkAllowed())
        {
            await DisplayAlertAsync("Sin red permitida",
                _settings.WifiOnly
                    ? "El respaldo está limitado a WiFi y no hay una red WiFi disponible. Puedes cambiarlo en Ajustes."
                    : "No hay conexión a internet.",
                "OK");
            return;
        }

        ApplyRunningState(true);
        ProgressFileLabel.Text = "Buscando archivos…";
        ProgressBytesLabel.Text = "El respaldo continúa aunque cierres la app.";
        FileProgressBar.Progress = 0;
        ErrorsLabel.IsVisible = false;

        _background.StartNow();
    }

    private async void OnRetryFailedClicked(object? sender, EventArgs e)
    {
        if (_sync.IsRunning)
            return;
        if (!await MediaPermissions.EnsureGrantedAsync())
            return;
        await _background.EnsureNotificationPermissionAsync();
        if (!_sync.IsNetworkAllowed())
        {
            await DisplayAlertAsync("Sin red permitida",
                _settings.WifiOnly
                    ? "El respaldo está limitado a WiFi y no hay una red WiFi disponible."
                    : "No hay conexión a internet.",
                "OK");
            return;
        }

        ApplyRunningState(true);
        ProgressFileLabel.Text = "Reintentando archivos con error…";
        ProgressBytesLabel.Text = "El respaldo continúa aunque cierres la app.";
        FileProgressBar.Progress = 0;
        ErrorsLabel.IsVisible = false;
        _background.StartNow(retryFailedOnly: true);
    }

    private void OnCancelClicked(object? sender, EventArgs e)
    {
        _background.Stop();
        ProgressBytesLabel.Text = "Deteniendo…";
    }

    private void OnProgressed(SyncProgress p) => MainThread.BeginInvokeOnMainThread(() =>
    {
        ApplyRunningState(true);
        ProgressFileLabel.Text = $"({p.FileIndex}/{p.FileCount}) {p.FileName}";
        FileProgressBar.Progress = p.FileBytesTotal > 0
            ? (double)p.FileBytesSent / p.FileBytesTotal
            : 0;
        ProgressBytesLabel.Text =
            $"{p.Status}  {FormatBytes(p.FileBytesSent)} / {FormatBytes(p.FileBytesTotal)}";
    });

    private void OnFinished(SyncSummary? summary) => MainThread.BeginInvokeOnMainThread(() =>
    {
        ApplyRunningState(false);
        RefreshLastRun();
        if (summary is { Errors: > 0 })
        {
            ErrorsLabel.Text = string.Join("\n", summary.ErrorDetails.Take(10));
            ErrorsLabel.IsVisible = true;
        }
    });

    private void ApplyRunningState(bool running)
    {
        BackupButton.IsEnabled = !running;
        CancelButton.IsVisible = running;
        ProgressPanel.IsVisible = running;
        RetryFailedButton.IsVisible = !running && _settings.FailedKeys.Count > 0;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.0} KB",
        _ => $"{bytes} B",
    };
}
