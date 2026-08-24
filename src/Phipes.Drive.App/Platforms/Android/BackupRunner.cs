using Phipes.Drive.App.Services;

namespace Phipes.Drive.App.Droid;

public enum BackupOutcome
{
    /// <summary>A pass ran to the end (it may still have skipped everything).</summary>
    Completed,

    /// <summary>Nothing to do: no session, no allowed network, or a pass was already running.</summary>
    Skipped,

    /// <summary>The stored session is gone; the user has to log in again.</summary>
    SessionExpired,

    Cancelled,

    Failed,
}

public sealed record BackupRunResult(BackupOutcome Outcome, SyncSummary? Summary, string? Message);

/// <summary>
/// One background backup pass, shared by the foreground service and by the WorkManager
/// fallback. Resolves its dependencies from the MAUI service provider, which exists as soon
/// as the Android Application object is created -- no Activity needed.
/// </summary>
public static class BackupRunner
{
    public static async Task<BackupRunResult> RunAsync(
        IProgress<SyncProgress>? progress, CancellationToken ct, bool onlyFailed = false)
    {
        var services = IPlatformApplication.Current?.Services;
        if (services is null)
            return new BackupRunResult(BackupOutcome.Failed, null, "La aplicación no está lista.");

        ApiClient api;
        SyncService sync;
        BackupSettings settings;
        try
        {
            api = services.GetRequiredService<ApiClient>();
            sync = services.GetRequiredService<SyncService>();
            settings = services.GetRequiredService<BackupSettings>();
        }
        catch (Exception ex)
        {
            return new BackupRunResult(BackupOutcome.Failed, null, ex.Message);
        }

        try
        {
            if (!await api.HasSessionAsync())
                return new BackupRunResult(BackupOutcome.Skipped, null, "Sin sesión iniciada.");
            if (!sync.IsNetworkAllowed())
                return new BackupRunResult(
                    BackupOutcome.Skipped,
                    null,
                    settings.WifiOnly ? "Esperando una red WiFi." : "Sin conexión a internet.");

            var summary = await sync.RunAsync(progress, ct, onlyFailed);
            return summary is null
                ? new BackupRunResult(BackupOutcome.Skipped, null, "Ya hay un respaldo en curso.")
                : new BackupRunResult(BackupOutcome.Completed, summary, null);
        }
        catch (OperationCanceledException)
        {
            return new BackupRunResult(BackupOutcome.Cancelled, null, "Respaldo detenido.");
        }
        catch (SessionExpiredException ex)
        {
            return new BackupRunResult(BackupOutcome.SessionExpired, null, ex.Message);
        }
        catch (Exception ex)
        {
            return new BackupRunResult(BackupOutcome.Failed, null, ex.Message);
        }
    }

    /// <summary>Human summary in Spanish for the closing notification.</summary>
    public static string Describe(SyncSummary summary)
    {
        var moved = summary.Uploaded + summary.Resumed;
        var text = moved switch
        {
            0 => "No había fotos nuevas.",
            1 => "Se respaldó 1 foto.",
            _ => $"Se respaldaron {moved} fotos.",
        };
        if (summary.Errors > 0)
            text += $" {summary.Errors} con problemas.";
        return text;
    }
}
