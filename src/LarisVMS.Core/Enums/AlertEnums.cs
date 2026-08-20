namespace LarisVMS.Core.Enums;

/// <summary>What an AlertRule watches for. Deliberately scoped to signals the app already computes
/// somewhere (DashboardService's camera health-freshness/node-online windows, Node's own storage
/// stats) rather than inventing new instrumentation — a rate-based condition like "reconnects per
/// hour" is dropped from this first pass since ReconnectCount is cumulative-since-connect, not a
/// rate, and turning it into one needs delta-tracking state this pass doesn't add.</summary>
public enum AlertConditionType
{
    /// <summary>The camera's Main stream hasn't reported health within DashboardService's own
    /// freshness window — the same condition that drives the Dashboard's "not reporting" count.</summary>
    CameraNotReporting = 0,

    /// <summary>The node hasn't heartbeated within DashboardService's own online window — the same
    /// condition that drives the Dashboard's and Admin/Nodes' own online/offline badge.</summary>
    NodeOffline = 1,

    /// <summary>The node's last-reported free space fraction has dropped below the rule's
    /// ThresholdPercent.</summary>
    NodeStorageLow = 2
}

/// <summary>Where an AlertDelivery sends a firing. BrowserPush is deliberately not in this list yet —
/// it needs VAPID key generation, a service worker, and per-browser PushSubscription storage, real
/// scope of its own rather than a config-blob variant of the other five.</summary>
public enum AlertChannel
{
    Email = 0,
    Webhook = 1,
    Ntfy = 2,
    Pushover = 3,
    Slack = 4,
    Teams = 5
}
