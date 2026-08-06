namespace Phipes.Drive.Core.Storage;

/// <summary>
/// Storage backend abstraction. Paths are always relative to the storage root,
/// use '/' as separator and never escape the root.
/// </summary>
public interface IFileStorage
{
    Task<IReadOnlyList<StorageItem>> ListAsync(string relativePath, CancellationToken ct = default);

    Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct = default);

    /// <summary>Writes <paramref name="content"/> to the given path, overwriting if it exists.</summary>
    Task SaveAsync(string relativePath, Stream content, CancellationToken ct = default);

    Task CreateFolderAsync(string relativePath, CancellationToken ct = default);

    /// <summary>Moves a file or folder into the trash area. Returns the trash entry id.</summary>
    Task<string> MoveToTrashAsync(string relativePath, CancellationToken ct = default);

    Task<IReadOnlyList<TrashEntry>> ListTrashAsync(CancellationToken ct = default);

    /// <summary>Restores a trash entry to its original location.</summary>
    Task RestoreAsync(string trashEntryId, CancellationToken ct = default);

    Task<StorageUsage> GetUsageAsync(CancellationToken ct = default);
}

public sealed record TrashEntry(string Id, string OriginalPath, DateTimeOffset DeletedUtc, long SizeBytes);

public sealed record StorageUsage(long UsedBytes, long? QuotaBytes);
