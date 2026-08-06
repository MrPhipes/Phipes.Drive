namespace Phipes.Drive.Core.Storage;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Absolute or relative path of the storage root. Created if missing.</summary>
    public string RootPath { get; set; } = "storage";

    /// <summary>Optional hard quota for the whole storage, in bytes. Null = unlimited.</summary>
    public long? QuotaBytes { get; set; }

    /// <summary>Name of the hidden folder (under the root) that holds trashed items.</summary>
    public string TrashFolderName { get; set; } = ".trash";
}
