using Microsoft.Maui.ApplicationModel;

namespace MinRepoMobile.Platforms.Android;

/// <summary>
/// Android 13以降で完了・失敗通知を表示するための権限です。
/// </summary>
public sealed class NotificationPermission : Permissions.BasePlatformPermission
{
    public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
    [
        (global::Android.Manifest.Permission.PostNotifications, true),
    ];
}

