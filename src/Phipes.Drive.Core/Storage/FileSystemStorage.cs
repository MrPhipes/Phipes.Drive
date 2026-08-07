namespace Phipes.Drive.Core.Storage;

/// <summary>
/// <see cref="IFileStorage"/> backed by a directory tree on a local or mounted filesystem
/// (e.g. a NAS dataset). All paths are validated so they can never escape the root.
/// </summary>
public sealed class FileSystemStorage : IFileStorage
{
    private readonly string _root;
    private readonly string _trashRoot;
    private readonly StorageOptions _options;

    public FileSystemStorage(StorageOptions options)
    {
        _options = options;
        _root = Path.GetFullPath(options.RootPath);
        _trashRoot = Path.Combine(_root, options.TrashFolderName);
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_trashRoot);
    }

    public Task<IReadOnlyList<StorageItem>> ListAsync(string relativePath, CancellationToken ct = default)
    {
        var dir = ResolveDirectory(relativePath);
        var items = new List<StorageItem>();
        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            if (IsTrash(entry.FullName)) continue;
            var rel = ToRelative(entry.FullName);
            items.Add(entry switch
            {
                DirectoryInfo d => new StorageItem(d.Name, rel, true, DirectorySize(d), d.LastWriteTimeUtc),
                FileInfo f => new StorageItem(f.Name, rel, false, f.Length, f.LastWriteTimeUtc),
                _ => throw new InvalidOperationException(),
            });
        }

        IReadOnlyList<StorageItem> result = items
            .OrderByDescending(i => i.IsFolder)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<StorageItem?> StatAsync(string relativePath, CancellationToken ct = default)
    {
        var path = Resolve(relativePath);
        StorageItem? item = null;
        if (Directory.Exists(path))
        {
            var d = new DirectoryInfo(path);
            item = new StorageItem(d.Name, ToRelative(path), true, 0, d.LastWriteTimeUtc);
        }
        else if (File.Exists(path))
        {
            var f = new FileInfo(path);
            item = new StorageItem(f.Name, ToRelative(path), false, f.Length, f.LastWriteTimeUtc);
        }
        return Task.FromResult(item);
    }

    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct = default)
    {
        var path = ResolveFile(relativePath);
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        return Task.FromResult(stream);
    }

    public async Task SaveAsync(string relativePath, Stream content, CancellationToken ct = default)
    {
        var path = Resolve(relativePath);
        if (Path.GetFileName(path).Length == 0)
            throw new ArgumentException("Path must include a file name.", nameof(relativePath));

        if (_options.QuotaBytes is { } quota)
        {
            var usage = await GetUsageAsync(ct).ConfigureAwait(false);
            if (content.CanSeek && usage.UsedBytes + content.Length > quota)
                throw new StorageQuotaExceededException(quota);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Write to a temp sibling first so a cancelled upload never leaves a half file.
        var temp = path + ".uploading";
        try
        {
            await using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None,
                             bufferSize: 81920, useAsync: true))
            {
                await content.CopyToAsync(target, ct).ConfigureAwait(false);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            throw;
        }
    }

    public async Task AppendAsync(string relativePath, Stream content, CancellationToken ct = default)
    {
        var path = ResolveFile(relativePath);
        await using var target = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);
        await content.CopyToAsync(target, ct).ConfigureAwait(false);
    }

    public Task CreateFolderAsync(string relativePath, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Resolve(relativePath));
        return Task.CompletedTask;
    }

    public Task<string> MoveToTrashAsync(string relativePath, CancellationToken ct = default)
    {
        var path = Resolve(relativePath);
        var isFolder = Directory.Exists(path);
        if (!isFolder && !File.Exists(path))
            throw new FileNotFoundException("Item not found.", relativePath);

        var id = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        var entryDir = Path.Combine(_trashRoot, id);
        Directory.CreateDirectory(entryDir);
        File.WriteAllText(Path.Combine(entryDir, "origin.txt"), NormalizeRelative(relativePath));

        var target = Path.Combine(entryDir, Path.GetFileName(path));
        if (isFolder) Directory.Move(path, target);
        else File.Move(path, target);
        return Task.FromResult(id);
    }

    public Task<IReadOnlyList<TrashEntry>> ListTrashAsync(CancellationToken ct = default)
    {
        var entries = new List<TrashEntry>();
        foreach (var dir in new DirectoryInfo(_trashRoot).EnumerateDirectories())
        {
            ct.ThrowIfCancellationRequested();
            var originFile = Path.Combine(dir.FullName, "origin.txt");
            if (!File.Exists(originFile)) continue;
            var origin = File.ReadAllText(originFile).Trim();
            var size = DirectorySize(dir);
            entries.Add(new TrashEntry(dir.Name, origin, dir.CreationTimeUtc, size));
        }

        IReadOnlyList<TrashEntry> result = entries.OrderByDescending(e => e.DeletedUtc).ToList();
        return Task.FromResult(result);
    }

    public Task RestoreAsync(string trashEntryId, CancellationToken ct = default)
    {
        if (trashEntryId.Contains('/') || trashEntryId.Contains('\\') || trashEntryId.Contains(".."))
            throw new ArgumentException("Invalid trash entry id.", nameof(trashEntryId));

        var entryDir = Path.Combine(_trashRoot, trashEntryId);
        var originFile = Path.Combine(entryDir, "origin.txt");
        if (!File.Exists(originFile))
            throw new FileNotFoundException("Trash entry not found.", trashEntryId);

        var origin = Resolve(File.ReadAllText(originFile).Trim());
        var payload = new DirectoryInfo(entryDir).EnumerateFileSystemInfos()
            .Single(e => e.Name != "origin.txt");

        Directory.CreateDirectory(Path.GetDirectoryName(origin)!);
        if (payload is DirectoryInfo) Directory.Move(payload.FullName, origin);
        else File.Move(payload.FullName, origin, overwrite: false);
        Directory.Delete(entryDir, recursive: true);
        return Task.CompletedTask;
    }

    public Task<StorageUsage> GetUsageAsync(CancellationToken ct = default)
    {
        var used = DirectorySize(new DirectoryInfo(_root));
        return Task.FromResult(new StorageUsage(used, _options.QuotaBytes));
    }

    private static long DirectorySize(DirectoryInfo dir)
    {
        long total = 0;
        foreach (var f in dir.EnumerateFiles("*", SearchOption.AllDirectories))
            total += f.Length;
        return total;
    }

    private bool IsTrash(string fullPath) =>
        string.Equals(Path.GetFullPath(fullPath), _trashRoot, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRelative(string relativePath) =>
        relativePath.Replace('\\', '/').Trim('/');

    /// <summary>Resolves a user-supplied relative path to an absolute path, guaranteed under the root.</summary>
    private string Resolve(string relativePath)
    {
        var raw = (relativePath ?? string.Empty).Replace('\\', '/');
        // Reject rooted paths from either platform convention, regardless of the OS we run on
        // (on Linux, IsPathRooted does not flag "C:\..."). Rejecting ':' also blocks NTFS
        // alternate data stream syntax like "file.txt:hidden".
        if (raw.StartsWith('/') || raw.Contains(':') || Path.IsPathRooted(raw))
            throw new StoragePathException(relativePath!);
        var normalized = NormalizeRelative(raw);

        var full = Path.GetFullPath(Path.Combine(_root, normalized));
        if (!full.Equals(_root, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new StoragePathException(relativePath!);
        if (full.StartsWith(_trashRoot, StringComparison.OrdinalIgnoreCase))
            throw new StoragePathException(relativePath!);
        return full;
    }

    private string ResolveDirectory(string relativePath)
    {
        var path = Resolve(relativePath);
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"Folder not found: {NormalizeRelative(relativePath)}");
        return path;
    }

    private string ResolveFile(string relativePath)
    {
        var path = Resolve(relativePath);
        if (!File.Exists(path))
            throw new FileNotFoundException("File not found.", NormalizeRelative(relativePath));
        return path;
    }

    private string ToRelative(string fullPath) =>
        Path.GetRelativePath(_root, fullPath).Replace('\\', '/');
}

public sealed class StoragePathException(string path)
    : ArgumentException($"Path is outside the storage root: '{path}'");

public sealed class StorageQuotaExceededException(long quotaBytes)
    : InvalidOperationException($"Storage quota of {quotaBytes} bytes exceeded.");
