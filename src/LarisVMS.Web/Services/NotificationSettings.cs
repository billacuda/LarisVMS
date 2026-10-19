using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Notifications;
using LarisVMS.Infrastructure.Security;

namespace LarisVMS.Web.Services;

/// <summary>Settings → Notifications: which notification types users may receive, and which delivery
/// transports are on. Secrets (the VAPID private key, the Firebase service account) are stored
/// encrypted with SecretProtection, the same as other credentials.</summary>
public static class NotificationSettings
{
    public const string OnPremEnabled = "Notifications.OnPrem.Enabled";
    public const string EmailEnabled = "Notifications.Email.Enabled";
    public const string WebPushEnabled = "Notifications.WebPush.Enabled";
    public const string WebPushPublicKey = "Notifications.WebPush.PublicKey";
    public const string WebPushPrivateKey = "Notifications.WebPush.PrivateKey";
    public const string WebPushSubject = "Notifications.WebPush.Subject";
    public const string FcmEnabled = "Notifications.Fcm.Enabled";
    public const string FcmWebConfig = "Notifications.Fcm.WebConfigJson";
    public const string FcmVapidKey = "Notifications.Fcm.VapidKey";
    public const string FcmServiceAccount = "Notifications.Fcm.ServiceAccountJson";

    public static string TypeEnabledKey(AlertConditionType type) => $"Notifications.Type.{type}.Enabled";

    /// <summary>Notification types users can receive, in the order Settings and the editors show them.</summary>
    public static readonly IReadOnlyList<(AlertConditionType Type, string Label)> Types =
    [
        (AlertConditionType.MotionDetected, "Motion detected"),
        (AlertConditionType.ObjectDetected, "Object detected"),
        (AlertConditionType.SpeedOver, "Speed over a limit"),
        (AlertConditionType.CameraNotReporting, "Camera not reporting"),
        (AlertConditionType.NodeOffline, "Node offline"),
        (AlertConditionType.NodeStorageLow, "Node storage low"),
        (AlertConditionType.NodeFailoverActivated, "Node failover"),
        (AlertConditionType.EvidenceLockExpiringSoon, "Evidence lock ending soon"),
        (AlertConditionType.EvidenceLockRemoved, "Evidence lock removed"),
    ];

    public static string LabelOf(AlertConditionType type) => Types.FirstOrDefault(t => t.Type == type).Label ?? type.ToString();

    public static Task<bool> IsTypeEnabledAsync(ISettingsResolver settings, AlertConditionType type, CancellationToken ct = default)
        => settings.GetAsync(TypeEnabledKey(type), true, ct: ct);

    public sealed record Snapshot(
        bool OnPrem, bool Email, bool WebPush, WebPushProtocol.VapidKeys? Vapid, string Subject,
        bool Fcm, string? FcmWebConfigJson, string? FcmPublicVapidKey, FcmClient.ServiceAccount? FcmAccount);

    /// <summary>Everything the delivery code needs, resolved once per firing. A transport switched on
    /// but missing its keys reads as off.</summary>
    public static async Task<Snapshot> LoadAsync(ISettingsResolver settings, CancellationToken ct = default)
    {
        var publicKey = await settings.GetRawAsync(WebPushPublicKey, ct: ct);
        var privateKey = SecretProtection.Unprotect(await settings.GetRawAsync(WebPushPrivateKey, ct: ct));
        var vapid = !string.IsNullOrEmpty(publicKey) && !string.IsNullOrEmpty(privateKey) ? new WebPushProtocol.VapidKeys(publicKey, privateKey) : null;

        FcmClient.ServiceAccount? account = null;
        var accountJson = SecretProtection.Unprotect(await settings.GetRawAsync(FcmServiceAccount, ct: ct));
        if (!string.IsNullOrWhiteSpace(accountJson))
        {
            try { account = FcmClient.ParseServiceAccount(accountJson); } catch (ArgumentException) { /* shown on the settings page */ }
        }
        var fcmWebConfig = await settings.GetRawAsync(FcmWebConfig, ct: ct);
        var fcmVapid = await settings.GetRawAsync(FcmVapidKey, ct: ct);

        return new Snapshot(
            OnPrem: await settings.GetAsync(OnPremEnabled, true, ct: ct),
            Email: await settings.GetAsync(EmailEnabled, true, ct: ct),
            WebPush: await settings.GetAsync(WebPushEnabled, false, ct: ct) && vapid is not null,
            Vapid: vapid,
            Subject: await settings.GetAsync(WebPushSubject, "mailto:admin@localhost", ct: ct),
            Fcm: await settings.GetAsync(FcmEnabled, false, ct: ct) && account is not null && !string.IsNullOrWhiteSpace(fcmWebConfig),
            FcmWebConfigJson: fcmWebConfig,
            FcmPublicVapidKey: fcmVapid,
            FcmAccount: account);
    }
}
