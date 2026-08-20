namespace LarisVMS.Infrastructure.Alerts;

/// <summary>The shape each AlertDelivery.ConfigJson serializes to/from, one record per AlertChannel —
/// same role as SmtpEmailConfig/GraphEmailConfig/GmailEmailConfig in LarisVMS.Infrastructure.Email.</summary>
public record EmailDeliveryConfig(string To);

public record WebhookDeliveryConfig(string Url);

/// <summary>ServerUrl defaults to the public ntfy.sh instance when null — a self-hosted ntfy server
/// is the only reason to set it.</summary>
public record NtfyDeliveryConfig(string Topic, string? ServerUrl);

public record PushoverDeliveryConfig(string AppToken, string UserKey);

public record SlackDeliveryConfig(string WebhookUrl);

public record TeamsDeliveryConfig(string WebhookUrl);
