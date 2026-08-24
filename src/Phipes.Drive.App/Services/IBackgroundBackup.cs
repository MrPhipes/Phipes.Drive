namespace Phipes.Drive.App.Services;

/// <summary>
/// Control surface for the unattended backup: schedule the triggers, start or stop a pass
/// right now, and manage the two system permissions it depends on (notifications and the
/// battery-optimisation exemption).
/// </summary>
public interface IBackgroundBackup
{
    /// <summary>False on platforms where unattended backup is not implemented yet.</summary>
    bool IsSupported { get; }

    /// <summary>True while a background pass is uploading.</summary>
    bool IsRunning { get; }

    /// <summary>Registers the periodic and opportunistic triggers. Idempotent.</summary>
    void Schedule();

    /// <summary>Runs a pass now, in the background, with the progress notification.</summary>
    /// <param name="retryFailedOnly">Only re-attempt the items that failed in the last pass.</param>
    void StartNow(bool retryFailedOnly = false);

    /// <summary>Asks the running pass to stop.</summary>
    void Stop();

    /// <summary>Removes every trigger (logout).</summary>
    void CancelAll();

    /// <summary>True when the system will let the app work freely in the background.</summary>
    bool IsBatteryOptimizationIgnored { get; }

    /// <summary>Opens the system dialog that grants the battery-optimisation exemption.</summary>
    Task RequestIgnoreBatteryOptimizationsAsync();

    /// <summary>Asks for POST_NOTIFICATIONS (Android 13+). True when granted.</summary>
    Task<bool> EnsureNotificationPermissionAsync();

    /// <summary>True when the app may post notifications.</summary>
    Task<bool> HasNotificationPermissionAsync();
}

/// <summary>Fallback for platforms without unattended backup (iOS pending).</summary>
public sealed class NullBackgroundBackup : IBackgroundBackup
{
    public bool IsSupported => false;

    public bool IsRunning => false;

    public void Schedule()
    {
    }

    public void StartNow(bool retryFailedOnly = false)
    {
    }

    public void Stop()
    {
    }

    public void CancelAll()
    {
    }

    public bool IsBatteryOptimizationIgnored => true;

    public Task RequestIgnoreBatteryOptimizationsAsync() => Task.CompletedTask;

    public Task<bool> EnsureNotificationPermissionAsync() => Task.FromResult(true);

    public Task<bool> HasNotificationPermissionAsync() => Task.FromResult(true);
}
