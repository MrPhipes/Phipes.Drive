using System.Diagnostics;
using System.Globalization;

namespace Phipes.Drive.Core.Photos;

/// <summary>
/// Thin wrapper over the ffmpeg/ffprobe executables. Everything is optional: when the binaries are
/// not installed the pipeline keeps working and simply produces no video thumbnails or durations.
/// </summary>
public sealed class Ffmpeg(PhotoOptions options, TimeSpan? timeout = null)
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(60);
    private bool? _ffmpegAvailable;
    private bool? _ffprobeAvailable;

    public bool IsAvailable => _ffmpegAvailable ??= Probe(options.FfmpegPath);

    public bool IsProbeAvailable => _ffprobeAvailable ??= Probe(options.FfprobePath);

    /// <summary>Writes the poster frame of <paramref name="inputPath"/> to <paramref name="outputPath"/>.</summary>
    public async Task<bool> TryExtractFrameAsync(string inputPath, string outputPath, CancellationToken ct = default)
    {
        if (!IsAvailable) return false;

        var seconds = options.VideoFrameSeconds.ToString("0.###", CultureInfo.InvariantCulture);

        // -ss before -i seeks by keyframe (fast). Clips shorter than the seek point produce no
        // frame at all, so fall back to decoding from the very beginning.
        if (await FrameAsync(["-ss", seconds, "-i", inputPath], outputPath, ct).ConfigureAwait(false))
            return true;
        return await FrameAsync(["-i", inputPath], outputPath, ct).ConfigureAwait(false);
    }

    private async Task<bool> FrameAsync(string[] input, string outputPath, CancellationToken ct)
    {
        string[] arguments = ["-hide_banner", "-loglevel", "error", "-y", .. input, "-frames:v", "1", "-an", outputPath];
        var ok = await RunAsync(options.FfmpegPath, arguments, ct).ConfigureAwait(false);
        return ok && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
    }

    /// <summary>Decodes the first frame of any file ffmpeg understands (HEIC included) to a PNG.</summary>
    public Task<bool> TryDecodeImageAsync(string inputPath, string outputPath, CancellationToken ct = default) =>
        IsAvailable
            ? RunAsync(options.FfmpegPath,
                ["-hide_banner", "-loglevel", "error", "-y", "-i", inputPath, "-frames:v", "1", outputPath], ct)
            : Task.FromResult(false);

    public async Task<long?> TryGetDurationMsAsync(string inputPath, CancellationToken ct = default)
    {
        if (!IsProbeAvailable) return null;

        var (ok, stdout) = await RunCapturingAsync(options.FfprobePath,
            ["-v", "error", "-show_entries", "format=duration", "-of",
                "default=noprint_wrappers=1:nokey=1", inputPath], ct).ConfigureAwait(false);

        if (!ok) return null;
        return double.TryParse(stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
               && seconds > 0
            ? (long)Math.Round(seconds * 1000)
            : null;
    }

    private static bool Probe(string executable)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return false;
            if (!process.WaitForExit(10_000)) { TryKill(process); return false; }
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> RunAsync(string executable, string[] arguments, CancellationToken ct) =>
        (await RunCapturingAsync(executable, arguments, ct).ConfigureAwait(false)).Ok;

    private async Task<(bool Ok, string StdOut)> RunCapturingAsync(
        string executable, string[] arguments, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return (false, string.Empty);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                var stdout = await stdoutTask.ConfigureAwait(false);
                await stderrTask.ConfigureAwait(false);
                return (process.ExitCode == 0, stdout);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return (false, string.Empty);
            }
        }
        catch
        {
            return (false, string.Empty);
        }
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }
}
