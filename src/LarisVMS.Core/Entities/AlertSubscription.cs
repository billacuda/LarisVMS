namespace LarisVMS.Core.Entities;

/// <summary>
/// One user receiving one AlertRule's firings as personal notifications — in the browser, by email,
/// or both. A user subscribes to a shared rule themselves (when it allows that) or an admin adds them;
/// the owner of a personal rule is subscribed automatically. Each firing still checks that the user
/// can view the camera involved, so a subscription never leaks footage events past camera access.
/// </summary>
public class AlertSubscription
{
    public Guid Id { get; set; }
    public Guid AlertRuleId { get; set; }

    /// <summary>ApplicationUser.Id.</summary>
    public string UserId { get; set; } = string.Empty;

    public bool Browser { get; set; } = true;
    public bool Email { get; set; }

    /// <summary>Muted until this time (UTC); null = not muted. DateTime.MaxValue = muted until the
    /// user turns it back on.</summary>
    public DateTime? MutedUntilUtc { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Who added the subscription (the user themselves, or an admin) — for the audit trail.</summary>
    public string? CreatedBy { get; set; }

    public AlertRule AlertRule { get; set; } = null!;
}

public enum PushSubscriptionKind { WebPush = 0, Fcm = 1 }

/// <summary>
/// One browser (or installed app) that can receive push notifications for a user while closed. Web
/// Push rows hold the browser's push endpoint and keys; Firebase rows hold the FCM registration token
/// in <see cref="Endpoint"/>. The on-premises transport needs no row — it reaches open tabs directly.
/// </summary>
public class PushSubscription
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public PushSubscriptionKind Kind { get; set; }

    /// <summary>The push service URL (Web Push) or the FCM registration token.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Web Push only: the browser's P-256 public key and auth secret, base64url.</summary>
    public string? P256dh { get; set; }
    public string? Auth { get; set; }

    /// <summary>A friendly label for the device list ("Chrome on Windows"), from the browser.</summary>
    public string? DeviceLabel { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSuccessAt { get; set; }
}
