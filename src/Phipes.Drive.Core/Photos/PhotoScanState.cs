namespace Phipes.Drive.Core.Photos;

/// <summary>Single-row bookkeeping for the incremental scanner.</summary>
public sealed class PhotoScanState
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public DateTimeOffset? LastScanUtc { get; set; }

    /// <summary>How long the last completed scan took, in milliseconds.</summary>
    public long LastScanDurationMs { get; set; }

    /// <summary>Media files seen on the last scan (indexed plus skipped duplicates).</summary>
    public int LastScanFileCount { get; set; }

    /// <summary>Files added or refreshed on the last scan.</summary>
    public int LastScanIndexedCount { get; set; }
}
