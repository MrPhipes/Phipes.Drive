using System.Collections.ObjectModel;
using Phipes.Drive.App.Services;
using Phipes.Drive.App.ViewModels;

namespace Phipes.Drive.App.Pages;

/// <summary>
/// The unified timeline: everything on the phone and everything on the NAS in a single
/// month-by-month grid. The device side is always rendered, even when the server is down.
/// </summary>
public partial class GalleryPage : ContentPage
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    private readonly GalleryService _gallery;
    private readonly ThumbnailLoader _loader;

    private readonly ObservableCollection<MonthGroup> _groups = [];
    private CancellationTokenSource? _cts;
    private DateTime _lastLoadUtc = DateTime.MinValue;
    private bool _loading;
    private bool _permissionAsked;

    public GalleryPage(GalleryService gallery, ThumbnailLoader loader)
    {
        InitializeComponent();
        _gallery = gallery;
        _loader = loader;
        ItemsView.ItemsSource = _groups;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loading)
            return;
        if (_lastLoadUtc == DateTime.MinValue)
        {
            await LoadAsync(refreshRemote: true, showSpinner: true);
            return;
        }
        if (DateTime.UtcNow - _lastLoadUtc > StaleAfter)
            await LoadAsync(refreshRemote: false, showSpinner: false);
    }

    private async void OnRefreshing(object? sender, EventArgs e)
    {
        await LoadAsync(refreshRemote: true, showSpinner: false);
        Refresher.IsRefreshing = false;
    }

    private async Task LoadAsync(bool refreshRemote, bool showSpinner)
    {
        if (_loading)
            return;
        _loading = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        if (showSpinner)
            Busy.IsVisible = Busy.IsRunning = true;

        try
        {
            if (!_permissionAsked)
            {
                _permissionAsked = true;
                if (!await MediaPermissions.EnsureGrantedAsync())
                {
                    ShowNotice("Sin permiso para leer fotos: solo se mostrará lo que esté en el NAS.");
                }
            }

            var result = await _gallery.LoadAsync(refreshRemote, ct);
            if (ct.IsCancellationRequested)
                return;

            Rebuild(result.Items);

            if (result.RemoteNotice is not null)
                ShowNotice(result.RemoteNotice);
            else
                HideNotice();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Never block the timeline on an unexpected failure: keep whatever is on screen.
            ShowNotice("No se pudo actualizar la galería. Se muestra lo último conocido.");
        }
        finally
        {
            _loading = false;
            _lastLoadUtc = DateTime.UtcNow;
            Busy.IsVisible = Busy.IsRunning = false;
        }
    }

    private void Rebuild(IReadOnlyList<GalleryItem> items)
    {
        _loader.Reset();
        _groups.Clear();

        var month = DateTime.MinValue;
        List<GalleryItemVm>? bucket = null;

        foreach (var item in items)
        {
            var itemMonth = new DateTime(item.TakenLocal.Year, item.TakenLocal.Month, 1);
            if (bucket is null || itemMonth != month)
            {
                if (bucket is not null)
                    _groups.Add(new MonthGroup(month, bucket));
                month = itemMonth;
                bucket = [];
            }
            bucket.Add(new GalleryItemVm(item, _loader));
        }

        if (bucket is { Count: > 0 })
            _groups.Add(new MonthGroup(month, bucket));
    }

    private void ShowNotice(string text)
    {
        NoticeLabel.Text = text;
        NoticeBanner.IsVisible = true;
    }

    private void HideNotice() => NoticeBanner.IsVisible = false;

    private async void OnItemTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Element)?.BindingContext is not GalleryItemVm vm)
            return;
        await Shell.Current.GoToAsync(
            nameof(ViewerPage), new Dictionary<string, object> { ["item"] = vm.Item });
    }
}
