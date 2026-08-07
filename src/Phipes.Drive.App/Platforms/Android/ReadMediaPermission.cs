using Android;

namespace Phipes.Drive.App.Droid;

/// <summary>
/// Media-read permission set: READ_MEDIA_IMAGES/VIDEO (+ ACCESS_MEDIA_LOCATION) on
/// Android 13+, READ_EXTERNAL_STORAGE on Android 10-12.
/// </summary>
public sealed class ReadMediaPermission : Permissions.BasePlatformPermission
{
    public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
        OperatingSystem.IsAndroidVersionAtLeast(33)
            ?
            [
                (Manifest.Permission.ReadMediaImages, true),
                (Manifest.Permission.ReadMediaVideo, true),
                (Manifest.Permission.AccessMediaLocation, true),
            ]
            :
            [
                (Manifest.Permission.ReadExternalStorage, true),
                (Manifest.Permission.AccessMediaLocation, true),
            ];
}
