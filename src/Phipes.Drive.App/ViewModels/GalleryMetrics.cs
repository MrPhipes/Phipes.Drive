namespace Phipes.Drive.App.ViewModels;

/// <summary>Grid geometry for the timeline: square tiles, three per row.</summary>
public static class GalleryMetrics
{
    public const int Columns = 3;

    private const double Spacing = 2;

    private static double? _tileHeight;

    /// <summary>Side of a tile in device-independent units.</summary>
    public static double TileHeight
    {
        get
        {
            if (_tileHeight is not null)
                return _tileHeight.Value;
            try
            {
                var info = DeviceDisplay.Current.MainDisplayInfo;
                var width = info.Width / (info.Density <= 0 ? 1 : info.Density);
                if (width > 0)
                    _tileHeight = (width - Spacing * (Columns - 1)) / Columns;
            }
            catch (Exception)
            {
                // Display metrics unavailable (headless): fall back to a sane tile.
            }
            return _tileHeight ??= 120;
        }
    }
}
