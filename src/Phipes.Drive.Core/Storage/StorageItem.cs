namespace Phipes.Drive.Core.Storage;

/// <summary>A file or folder as exposed by an <see cref="IFileStorage"/> provider.</summary>
public sealed record StorageItem(
    string Name,
    string RelativePath,
    bool IsFolder,
    long SizeBytes,
    DateTimeOffset ModifiedUtc);
