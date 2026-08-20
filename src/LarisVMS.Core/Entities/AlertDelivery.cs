using LarisVMS.Core.Enums;

namespace LarisVMS.Core.Entities;

/// <summary>
/// One delivery channel attached to an AlertRule. ConfigJson is an opaque, channel-shaped blob (a
/// webhook URL, an ntfy topic/server, a Pushover app/user token pair, a Slack/Teams incoming-webhook
/// URL) rather than normalized columns — genuinely justified here, unlike a single-account settings
/// row: channels are structurally unrelated to each other, and every one of them is a bearer secret
/// (anyone holding a Slack/Teams webhook URL can post to that channel), so the whole blob is
/// encrypted at rest through the same SecretProtection converter as everything else. Email has no
/// per-delivery config at all — it always sends through the singleton EmailSettings row, so
/// ConfigJson only carries the "to" address.
/// </summary>
public class AlertDelivery
{
    public Guid Id { get; set; }
    public Guid AlertRuleId { get; set; }
    public AlertChannel Channel { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string? ConfigJson { get; set; }

    public AlertRule AlertRule { get; set; } = null!;
}
