using Android.Content;
using AndroidXFileProvider = AndroidX.Core.Content.FileProvider;
using Phipes.Drive.App.Services;
using AndroidUri = Android.Net.Uri;
using Application = Android.App.Application;

namespace Phipes.Drive.App.Droid;

/// <summary>
/// Android share/open implementation. Device items travel as their MediaStore content URI
/// (nothing is copied); files pulled from the NAS live in the app cache and are exposed
/// through the FileProvider declared in the manifest.
/// </summary>
public sealed class MediaActions : IMediaActions
{
    private static string Authority =>
        Application.Context.PackageName + ".fileprovider";

    public Task ShareFileAsync(string filePath, string name, bool isVideo)
    {
        var uri = AndroidXFileProvider.GetUriForFile(
            Application.Context, Authority, new Java.IO.File(filePath))
            ?? throw new IOException("No se pudo preparar el archivo para compartir.");
        return ShareAsync(uri, MimeFor(name, isVideo));
    }

    public Task ShareDeviceItemAsync(MediaItem item) =>
        ShareAsync(AndroidUri.Parse(item.Uri)!, MimeFor(item.Name, item.IsVideo));

    public Task OpenFileAsync(string filePath, bool isVideo)
    {
        var uri = AndroidXFileProvider.GetUriForFile(
            Application.Context, Authority, new Java.IO.File(filePath))
            ?? throw new IOException("No se pudo abrir el archivo.");
        return OpenAsync(uri, MimeFor(filePath, isVideo));
    }

    public Task OpenDeviceItemAsync(MediaItem item) =>
        OpenAsync(AndroidUri.Parse(item.Uri)!, MimeFor(item.Name, item.IsVideo));

    private static Task ShareAsync(AndroidUri uri, string mimeType)
    {
        var send = new Intent(Intent.ActionSend);
        send.SetType(mimeType);
        send.PutExtra(Intent.ExtraStream, uri);
        send.AddFlags(ActivityFlags.GrantReadUriPermission);

        var chooser = Intent.CreateChooser(send, "Compartir")!;
        chooser.AddFlags(ActivityFlags.GrantReadUriPermission);
        StartActivity(chooser);
        return Task.CompletedTask;
    }

    private static Task OpenAsync(AndroidUri uri, string mimeType)
    {
        var view = new Intent(Intent.ActionView);
        view.SetDataAndType(uri, mimeType);
        view.AddFlags(ActivityFlags.GrantReadUriPermission);
        StartActivity(Intent.CreateChooser(view, "Abrir con")!);
        return Task.CompletedTask;
    }

    private static void StartActivity(Intent intent)
    {
        var activity = Platform.CurrentActivity;
        if (activity is not null)
        {
            activity.StartActivity(intent);
            return;
        }
        intent.AddFlags(ActivityFlags.NewTask);
        Application.Context.StartActivity(intent);
    }

    private static string MimeFor(string name, bool isVideo)
    {
        var extension = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        return extension switch
        {
            "jpg" or "jpeg" => "image/jpeg",
            "png" => "image/png",
            "gif" => "image/gif",
            "webp" => "image/webp",
            "heic" or "heif" => "image/heif",
            "dng" => "image/x-adobe-dng",
            "mp4" => "video/mp4",
            "mkv" => "video/x-matroska",
            "3gp" => "video/3gpp",
            "webm" => "video/webm",
            "mov" => "video/quicktime",
            _ => isVideo ? "video/*" : "image/*",
        };
    }
}
