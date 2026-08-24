using Phipes.Drive.App.Services;

namespace Phipes.Drive.App.Pages;

/// <summary>
/// Full-screen viewer. Shows a large preview first (device thumbnail or /thumb?size=1024),
/// and pulls the original out of the NAS on demand -- when the user zooms in, taps
/// "Ver original", plays a remote video, or shares an item that is only on the NAS.
/// </summary>
public partial class ViewerPage : ContentPage, IQueryAttributable
{
    private const double MaxScale = 5d;

    private readonly ThumbnailStore _thumbs;
    private readonly OriginalStore _originals;
    private readonly IMediaLibrary _media;
    private readonly IMediaActions _actions;

    private GalleryItem? _item;
    private string? _originalPath;
    private bool _showingOriginal;
    private bool _busy;

    private double _scale = 1d;
    private double _startScale = 1d;
    private double _panStartX;
    private double _panStartY;

    public ViewerPage(
        ThumbnailStore thumbs, OriginalStore originals, IMediaLibrary media, IMediaActions actions)
    {
        InitializeComponent();
        _thumbs = thumbs;
        _originals = originals;
        _media = media;
        _actions = actions;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("item", out var value) && value is GalleryItem item)
        {
            _item = item;
            _originalPath = _originals.IsDownloaded(item) ? _originals.PathFor(item) : null;
            MainThread.BeginInvokeOnMainThread(async () => await LoadAsync());
        }
    }

    private async Task LoadAsync()
    {
        if (_item is null)
            return;

        Title = _item.Name;
        NameLabel.Text = _item.Name;
        InfoLabel.Text = BuildInfo(_item);
        PlayButton.IsVisible = _item.IsVideo;
        OriginalButton.IsVisible = !_item.IsVideo;

        Busy.IsVisible = Busy.IsRunning = true;
        try
        {
            var local = _item.Local;
            byte[]? preview = local is not null
                ? await _media.GetThumbnailAsync(local, 1024)
                : _item.RemoteId is not null
                    ? await _thumbs.GetRemoteAsync(_item.RemoteId, 1024, CancellationToken.None)
                    : null;

            if (preview is { Length: > 0 })
                Preview.Source = ImageSource.FromStream(() => new MemoryStream(preview, writable: false));
            else if (local is not null)
                Preview.Source = ImageSource.FromStream(_ => _media.OpenReadAsync(local));
            else
                InfoLabel.Text = BuildInfo(_item) + " - vista previa no disponible";
        }
        catch (Exception)
        {
            InfoLabel.Text = BuildInfo(_item) + " - no se pudo cargar la vista previa";
        }
        finally
        {
            Busy.IsVisible = Busy.IsRunning = false;
        }
    }

    private static string BuildInfo(GalleryItem item)
    {
        var where = item.Location switch
        {
            MediaLocation.Both => "En el teléfono y en el NAS",
            MediaLocation.RemoteOnly => "Solo en el NAS",
            _ => "Solo en el teléfono",
        };
        return $"{item.TakenLocal:dd-MM-yyyy HH:mm} · {FormatBytes(item.SizeBytes)} · {where}";
    }

    // ------------------------------------------------------------------ actions

    private async void OnOriginalClicked(object? sender, EventArgs e) => await ShowOriginalAsync();

    private async void OnPlayClicked(object? sender, EventArgs e)
    {
        if (_item is null || _busy)
            return;
        try
        {
            if (_item.Local is not null)
            {
                await _actions.OpenDeviceItemAsync(_item.Local);
                return;
            }
            // Remote video: pull it down once, then hand it to the system player. Simpler and
            // far more predictable than streaming inside the app.
            var path = await EnsureOriginalAsync("Descargando video…");
            if (path is not null)
                await _actions.OpenFileAsync(path, isVideo: true);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Video", ex.Message, "OK");
        }
    }

    private async void OnShareClicked(object? sender, EventArgs e)
    {
        if (_item is null || _busy)
            return;
        try
        {
            if (_item.Local is not null)
            {
                await _actions.ShareDeviceItemAsync(_item.Local);
                return;
            }
            var path = await EnsureOriginalAsync("Trayendo el archivo del NAS…");
            if (path is not null)
                await _actions.ShareFileAsync(path, _item.Name, _item.IsVideo);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Compartir", ex.Message, "OK");
        }
    }

    private async Task ShowOriginalAsync()
    {
        if (_item is null || _busy || _showingOriginal || _item.IsVideo)
            return;

        try
        {
            var local = _item.Local;
            if (local is not null)
            {
                Preview.Source = ImageSource.FromStream(_ => _media.OpenReadAsync(local));
                _showingOriginal = true;
                return;
            }

            var path = await EnsureOriginalAsync("Descargando original…");
            if (path is null)
                return;
            Preview.Source = ImageSource.FromFile(path);
            _showingOriginal = true;
            OriginalButton.IsVisible = false;
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Original", ex.Message, "OK");
        }
    }

    /// <summary>Downloads the original with a progress bar; returns null when it fails.</summary>
    private async Task<string?> EnsureOriginalAsync(string caption)
    {
        if (_item is null)
            return null;
        if (_originalPath is not null && File.Exists(_originalPath))
            return _originalPath;
        if (_item.RemoteId is null)
            return null;

        _busy = true;
        DownloadPanel.IsVisible = true;
        DownloadBar.Progress = 0;
        DownloadLabel.Text = caption;
        ShareButton.IsEnabled = OriginalButton.IsEnabled = PlayButton.IsEnabled = false;

        var progress = new Progress<double>(value =>
        {
            DownloadBar.Progress = value;
            DownloadLabel.Text = $"{caption} {value * 100:0}%";
        });

        try
        {
            _originalPath = await _originals.EnsureAsync(_item, progress, CancellationToken.None);
            return _originalPath;
        }
        catch (SessionExpiredException)
        {
            await DisplayAlertAsync("Sesión", "La sesión expiró. Inicia sesión nuevamente.", "OK");
            await Shell.Current.GoToAsync("//login");
            return null;
        }
        catch (Exception)
        {
            await DisplayAlertAsync("Sin conexión",
                "No se pudo traer el archivo desde el NAS. Revisa tu conexión e inténtalo otra vez.",
                "OK");
            return null;
        }
        finally
        {
            _busy = false;
            DownloadPanel.IsVisible = false;
            ShareButton.IsEnabled = OriginalButton.IsEnabled = PlayButton.IsEnabled = true;
        }
    }

    // ------------------------------------------------------------------ zoom and pan

    private void OnPinchUpdated(object? sender, PinchGestureUpdatedEventArgs e)
    {
        switch (e.Status)
        {
            case GestureStatus.Started:
                _startScale = _scale;
                break;
            case GestureStatus.Running:
                _scale = Math.Clamp(_startScale * e.Scale, 1d, MaxScale);
                Preview.Scale = _scale;
                if (_scale <= 1d)
                    ResetPan();
                break;
            case GestureStatus.Completed:
                // Zooming in is the natural "I want to see this properly" gesture.
                if (_scale > 1.5d && !_showingOriginal)
                    _ = ShowOriginalAsync();
                break;
        }
    }

    private void OnPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        if (_scale <= 1d)
            return;
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _panStartX = Preview.TranslationX;
                _panStartY = Preview.TranslationY;
                break;
            case GestureStatus.Running:
                var limitX = ZoomHost.Width * (_scale - 1) / 2;
                var limitY = ZoomHost.Height * (_scale - 1) / 2;
                Preview.TranslationX = Math.Clamp(_panStartX + e.TotalX, -limitX, limitX);
                Preview.TranslationY = Math.Clamp(_panStartY + e.TotalY, -limitY, limitY);
                break;
        }
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_scale > 1d)
        {
            _scale = 1d;
            Preview.Scale = 1d;
            ResetPan();
            return;
        }
        _scale = 2.5d;
        Preview.Scale = _scale;
        if (!_showingOriginal)
            _ = ShowOriginalAsync();
    }

    private void ResetPan()
    {
        Preview.TranslationX = 0;
        Preview.TranslationY = 0;
        _panStartX = 0;
        _panStartY = 0;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.0} KB",
        _ => $"{bytes} B",
    };
}
