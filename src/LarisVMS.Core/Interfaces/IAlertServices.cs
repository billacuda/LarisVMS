using LarisVMS.Core.Enums;

namespace LarisVMS.Core.Interfaces;

/// <summary>
/// One sender strategy per AlertChannel, resolved by AlertChannelSenderFactory — same
/// dictionary-by-enum shape as IEmailProvider/EmailProviderFactory. configJson is the channel's own
/// opaque config blob from AlertDelivery.ConfigJson; ruleName/message are what actually fired.
/// </summary>
public interface IAlertChannelSender
{
    AlertChannel Channel { get; }
    Task SendAsync(string? configJson, string ruleName, string message, CancellationToken ct = default);

    /// <summary>The same, with what the alert is about for channels that send structured data (the
    /// webhook). Channels that only send text ignore the details.</summary>
    Task SendAsync(string? configJson, string ruleName, string message, AlertEventDetails? details, CancellationToken ct = default)
        => SendAsync(configJson, ruleName, message, ct);
}

/// <summary>What fired an alert: its condition, and the camera, node and detection when there is one.
/// SnapshotPath is a server-relative /api/v1 path to the detection's picture.</summary>
public sealed record AlertEventDetails(
    AlertConditionType Condition,
    Guid? CameraId = null,
    string? CameraName = null,
    Guid? NodeId = null,
    string? NodeName = null,
    string? Category = null,
    string? Label = null,
    long? SpanId = null,
    DateTime? StartedUtc = null,
    double? SpeedKph = null)
{
    public string? SnapshotPath => CameraId is { } camera && SpanId is { } span && Category is not null
        ? $"/api/v1/cameras/{camera}/events/{span}/snapshot"
        : null;
}
