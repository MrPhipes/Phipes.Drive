namespace Phipes.Drive.App.Services;

/// <summary>Where the bytes of a gallery item actually live.</summary>
public enum MediaLocation
{
    /// <summary>Only on the phone: it has not reached the NAS yet.</summary>
    LocalOnly,

    /// <summary>Only on the NAS: it was freed from the phone (or taken with another device).</summary>
    RemoteOnly,

    /// <summary>On the phone and on the NAS.</summary>
    Both,
}

/// <summary>
/// One entry of the unified timeline. Local and remote copies of the same picture are
/// merged into a single item, matched by file name plus exact byte size.
/// </summary>
public sealed class GalleryItem
{
    public required string Key { get; init; }

    public required string Name { get; init; }

    public required long SizeBytes { get; init; }

    /// <summary>Capture date in device local time; the timeline is sorted by this, descending.</summary>
    public required DateTime TakenLocal { get; init; }

    public required bool IsVideo { get; init; }

    /// <summary>The device copy, when there is one.</summary>
    public MediaItem? Local { get; init; }

    /// <summary>The server id (SHA-256 of the content), when the item is indexed on the NAS.</summary>
    public string? RemoteId { get; init; }

    /// <summary>Video length in milliseconds when known (server metadata only).</summary>
    public long DurationMs { get; init; }

    /// <summary>
    /// The backup ledger says this file was uploaded, even if the server index has not been
    /// read yet (or is unreachable). Lets the cloud badge stay honest while offline.
    /// </summary>
    public bool KnownBackedUp { get; init; }

    public MediaLocation Location =>
        Local is not null
            ? RemoteId is not null || KnownBackedUp ? MediaLocation.Both : MediaLocation.LocalOnly
            : MediaLocation.RemoteOnly;

    public bool HasLocal => Local is not null;

    public bool HasRemote => RemoteId is not null;
}
