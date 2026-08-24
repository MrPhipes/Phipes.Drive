using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Phipes.Drive.App.Services;

namespace Phipes.Drive.App.ViewModels;

/// <summary>
/// One tile of the timeline. The thumbnail is fetched the first time the tile is actually
/// bound by the virtualised grid, and can be dropped again when it scrolls far away.
/// </summary>
public sealed class GalleryItemVm(GalleryItem item, ThumbnailLoader loader) : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;
    private bool _requested;

    public event PropertyChangedEventHandler? PropertyChanged;

    public GalleryItem Item { get; } = item;

    public string Name => Item.Name;

    public bool IsVideo => Item.IsVideo;

    /// <summary>Square tile side in device units, so the grid keeps its 1:1 cells.</summary>
    public double TileHeight => GalleryMetrics.TileHeight;

    /// <summary>Thumbnail source. Reading it queues the load; it stays null until then.</summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (!_requested)
            {
                _requested = true;
                loader.Request(this);
            }
            else
            {
                loader.Touch(this);
            }
            return _thumbnail;
        }
    }

    /// <summary>Small badge telling where the item lives, without getting in the way.</summary>
    public string BadgeGlyph => Item.Location switch
    {
        MediaLocation.Both => "\u2601",       // cloud: on the phone and on the NAS
        MediaLocation.RemoteOnly => "\u2601", // cloud: only on the NAS
        _ => "\u2191",                        // up arrow: still waiting to be backed up
    };

    public Color BadgeColor => Item.Location switch
    {
        MediaLocation.Both => Colors.White,
        MediaLocation.RemoteOnly => Color.FromArgb("#90CAF9"),
        _ => Color.FromArgb("#FFC107"),
    };

    public string LocationText => Item.Location switch
    {
        MediaLocation.Both => "En el teléfono y en el NAS",
        MediaLocation.RemoteOnly => "Solo en el NAS",
        _ => "Solo en el teléfono",
    };

    public string DurationText
    {
        get
        {
            if (!Item.IsVideo || Item.DurationMs <= 0)
                return string.Empty;
            var span = TimeSpan.FromMilliseconds(Item.DurationMs);
            return span.TotalHours >= 1
                ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        }
    }

    public bool HasDuration => DurationText.Length > 0;

    internal void ApplyThumbnail(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return;
        // Keep the bytes, not a stream: MAUI may ask for the stream more than once.
        _thumbnail = ImageSource.FromStream(() => new MemoryStream(bytes, writable: false));
        Raise(nameof(Thumbnail));
    }

    internal void DropThumbnail()
    {
        if (_thumbnail is null)
            return;
        _thumbnail = null;
        _requested = false;
        Raise(nameof(Thumbnail));
    }

    private void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
