using Android.Content;
using Android.Database;
using Android.Provider;
using Phipes.Drive.App.Services;
using AndroidUri = Android.Net.Uri;

namespace Phipes.Drive.App.Droid;

/// <summary>Enumerates photos and videos through the Android MediaStore.</summary>
public sealed class MediaLibrary : IMediaLibrary
{
    private static ContentResolver Resolver =>
        global::Android.App.Application.Context.ContentResolver
        ?? throw new InvalidOperationException("ContentResolver no disponible.");

    private static readonly AndroidUri ImagesUri = MediaStore.Images.Media.ExternalContentUri!;
    private static readonly AndroidUri VideosUri = MediaStore.Video.Media.ExternalContentUri!;

    public Task<IReadOnlyList<string>> GetAvailableFoldersAsync() => Task.Run<IReadOnlyList<string>>(() =>
    {
        var folders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var uri in new[] { ImagesUri, VideosUri })
        {
            using var cursor = Resolver.Query(
                uri, [MediaStore.IMediaColumns.RelativePath], null, null, null);
            if (cursor is null)
                continue;
            while (cursor.MoveToNext())
            {
                var path = cursor.GetString(0);
                if (!string.IsNullOrEmpty(path))
                    folders.Add(path.Trim('/'));
            }
        }
        return folders.ToList();
    });

    public Task<IReadOnlyList<MediaItem>> GetItemsAsync(IReadOnlyCollection<string> folders) =>
        Task.Run<IReadOnlyList<MediaItem>>(() =>
        {
            var items = new List<MediaItem>();
            // MediaStore stores relative paths with a trailing slash, e.g. "DCIM/Camera/".
            var paths = folders.Select(f => f.Trim('/') + "/").ToArray();
            if (paths.Length == 0)
                return items;

            var placeholders = string.Join(",", Enumerable.Repeat("?", paths.Length));
            var selection = $"{MediaStore.IMediaColumns.RelativePath} IN ({placeholders})";

            QueryInto(items, ImagesUri, selection, paths, isVideo: false);
            QueryInto(items, VideosUri, selection, paths, isVideo: true);

            // Oldest first so an interrupted backup makes progress in a stable order.
            items.Sort((a, b) => a.TakenLocal.CompareTo(b.TakenLocal));
            return items;
        });

    private static void QueryInto(
        List<MediaItem> items, AndroidUri contentUri, string selection, string[] args, bool isVideo)
    {
        string[] projection =
        [
            "_id",
            MediaStore.IMediaColumns.DisplayName,
            MediaStore.IMediaColumns.Size,
            MediaStore.IMediaColumns.DateTaken,
            MediaStore.IMediaColumns.DateModified,
        ];

        using var cursor = Resolver.Query(contentUri, projection, selection, args, null);
        if (cursor is null)
            return;

        int idCol = cursor.GetColumnIndexOrThrow("_id");
        int nameCol = cursor.GetColumnIndexOrThrow(MediaStore.IMediaColumns.DisplayName);
        int sizeCol = cursor.GetColumnIndexOrThrow(MediaStore.IMediaColumns.Size);
        int takenCol = cursor.GetColumnIndexOrThrow(MediaStore.IMediaColumns.DateTaken);
        int modifiedCol = cursor.GetColumnIndexOrThrow(MediaStore.IMediaColumns.DateModified);

        while (cursor.MoveToNext())
        {
            var name = cursor.GetString(nameCol);
            var size = cursor.GetLong(sizeCol);
            if (string.IsNullOrEmpty(name) || size <= 0)
                continue;

            // Capture date in ms; fall back to file modification date (seconds).
            var takenMs = cursor.IsNull(takenCol) ? 0 : cursor.GetLong(takenCol);
            if (takenMs <= 0)
                takenMs = cursor.GetLong(modifiedCol) * 1000;
            var taken = takenMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(takenMs).LocalDateTime
                : DateTime.Now;

            var itemUri = ContentUris.WithAppendedId(contentUri, cursor.GetLong(idCol));
            items.Add(new MediaItem(itemUri.ToString()!, name, size, taken, isVideo));
        }
    }

    public Task<Stream> OpenReadAsync(MediaItem item)
    {
        var stream = Resolver.OpenInputStream(AndroidUri.Parse(item.Uri)!)
                     ?? throw new IOException($"No se pudo abrir {item.Name}.");
        return Task.FromResult(stream);
    }
}
