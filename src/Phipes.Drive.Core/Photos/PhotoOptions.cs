namespace Phipes.Drive.Core.Photos;

public sealed class PhotoOptions
{
    public const string SectionName = "Photos";

    /// <summary>Set to false to disable the background scanner entirely.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Where thumbnails live. Empty means "<c>&lt;RootPath&gt;/../thumbs</c>", i.e. a sibling of the
    /// storage root so generated files never show up in the user's file browser.
    /// </summary>
    public string? ThumbsPath { get; set; }

    /// <summary>Minutes between incremental scans. The first scan runs shortly after startup.</summary>
    public int ScanIntervalMinutes { get; set; } = 15;

    /// <summary>Executable used to decode video frames (and formats ImageSharp cannot read).</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>Executable used to read video duration. Optional; falls back to ffmpeg output parsing.</summary>
    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>Seconds into the video used as the poster frame.</summary>
    public double VideoFrameSeconds { get; set; } = 1;

    /// <summary>JPEG quality for generated thumbnails.</summary>
    public int JpegQuality { get; set; } = 80;
}
