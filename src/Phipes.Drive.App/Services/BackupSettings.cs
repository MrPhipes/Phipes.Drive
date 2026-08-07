using System.Text.Json;

namespace Phipes.Drive.App.Services;

/// <summary>User preferences for the backup: folders, Wi-Fi-only flag, last run summary.</summary>
public sealed class BackupSettings
{
    private const string WifiOnlyKey = "backup.wifi_only";
    private const string FoldersKey = "backup.folders";
    private const string LastRunKey = "backup.last_run_unix";
    private const string LastResultKey = "backup.last_result";

    public const string DefaultFolder = "DCIM/Camera";

    public bool WifiOnly
    {
        get => Preferences.Default.Get(WifiOnlyKey, true);
        set => Preferences.Default.Set(WifiOnlyKey, value);
    }

    public IReadOnlyList<string> Folders
    {
        get
        {
            var json = Preferences.Default.Get(FoldersKey, string.Empty);
            if (string.IsNullOrEmpty(json))
                return [DefaultFolder];
            try
            {
                return JsonSerializer.Deserialize<string[]>(json) is { Length: > 0 } list
                    ? list
                    : [DefaultFolder];
            }
            catch (JsonException)
            {
                return [DefaultFolder];
            }
        }
        set => Preferences.Default.Set(FoldersKey, JsonSerializer.Serialize(value));
    }

    public void AddFolder(string folder)
    {
        folder = Normalize(folder);
        var folders = Folders.ToList();
        if (!folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            folders.Add(folder);
            Folders = folders;
        }
    }

    public void RemoveFolder(string folder)
    {
        var folders = Folders.Where(f => !string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)).ToList();
        Folders = folders.Count > 0 ? folders : [DefaultFolder];
    }

    public DateTimeOffset? LastRunUtc
    {
        get
        {
            var unix = Preferences.Default.Get(LastRunKey, 0L);
            return unix == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(unix);
        }
        set => Preferences.Default.Set(LastRunKey, value?.ToUnixTimeSeconds() ?? 0L);
    }

    public string LastResult
    {
        get => Preferences.Default.Get(LastResultKey, string.Empty);
        set => Preferences.Default.Set(LastResultKey, value);
    }

    private static string Normalize(string folder) =>
        folder.Replace('\\', '/').Trim().Trim('/');
}
