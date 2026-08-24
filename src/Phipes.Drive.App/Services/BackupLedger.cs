using System.Collections.Concurrent;
using System.Text;

namespace Phipes.Drive.App.Services;

/// <summary>
/// Remembers which local items are already on the server so repeated background passes do
/// not re-stat every file. The ledger is an append-only text file of "name|size" keys; it is
/// only an optimisation, so a missing or corrupt file simply means a slower first pass.
/// </summary>
public sealed class BackupLedger
{
    private readonly string _path =
        Path.Combine(FileSystem.AppDataDirectory, "backup-ledger.txt");

    private readonly ConcurrentDictionary<string, byte> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _loaded;

    /// <summary>Stable identity for an item across devices: file name plus exact size.</summary>
    public static string KeyFor(string name, long sizeBytes) => $"{name}|{sizeBytes}";

    public void EnsureLoaded()
    {
        if (_loaded)
            return;
        _loaded = true;
        try
        {
            if (!File.Exists(_path))
                return;
            foreach (var line in File.ReadLines(_path))
            {
                if (line.Length > 0)
                    _keys.TryAdd(line, 0);
            }
        }
        catch (IOException)
        {
            // Best effort: an unreadable ledger just costs one slower pass.
        }
    }

    public bool Contains(string key)
    {
        EnsureLoaded();
        return _keys.ContainsKey(key);
    }

    public async Task AddAsync(string key)
    {
        EnsureLoaded();
        if (!_keys.TryAdd(key, 0))
            return;

        await _writeLock.WaitAsync();
        try
        {
            await File.AppendAllTextAsync(_path, key + "\n", Encoding.UTF8);
        }
        catch (IOException)
        {
            // Keep the in-memory entry; it will be rebuilt next launch.
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Every key known to be backed up (used by the gallery to mark local items).</summary>
    public IReadOnlyCollection<string> Keys
    {
        get
        {
            EnsureLoaded();
            return _keys.Keys.ToArray();
        }
    }

    public void Clear()
    {
        _keys.Clear();
        _loaded = true;
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        catch (IOException)
        {
        }
    }
}
