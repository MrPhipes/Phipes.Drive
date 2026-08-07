namespace Phipes.Drive.App.Services;

/// <summary>Requests the media-read permissions needed to enumerate photos and videos.</summary>
public static class MediaPermissions
{
    public static async Task<bool> EnsureGrantedAsync()
    {
#if ANDROID
        var status = await Permissions.CheckStatusAsync<Droid.ReadMediaPermission>();
        if (status != PermissionStatus.Granted)
            status = await Permissions.RequestAsync<Droid.ReadMediaPermission>();
        return status == PermissionStatus.Granted;
#else
        return await Task.FromResult(true);
#endif
    }
}
