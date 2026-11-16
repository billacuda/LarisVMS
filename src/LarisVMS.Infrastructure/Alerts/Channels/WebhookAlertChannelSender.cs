using System.Text.Json;
using System.Text.Json.Serialization;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Core.Localization;

namespace LarisVMS.Infrastructure.Alerts.Channels;

/// <summary>Generic outbound webhook — POSTs a small JSON envelope so any receiver (Home Assistant's
/// webhook trigger, a custom automation, a monitoring system's inbound-webhook endpoint) gets the rule
/// name, message and firing time, plus what fired it (event, camera, node, detection) when known,
/// without needing to parse a channel-specific payload shape. Fields that don't apply are left out.</summary>
public class WebhookAlertChannelSender : IAlertChannelSender
{
    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public AlertChannel Channel => AlertChannel.Webhook;

    public Task SendAsync(string? configJson, string ruleName, string message, CancellationToken ct = default)
        => SendAsync(configJson, ruleName, message, null, ct);

    public async Task SendAsync(string? configJson, string ruleName, string message, AlertEventDetails? details, CancellationToken ct = default)
    {
        var config = string.IsNullOrWhiteSpace(configJson)
            ? throw new InvalidOperationException(Loc.T("server.webhookAlertChannel.webhookDeliveryConfigurationIsMissing"))
            : JsonSerializer.Deserialize<WebhookDeliveryConfig>(configJson)
                ?? throw new InvalidOperationException(Loc.T("server.webhookAlertChannel.webhookDeliveryConfigurationIsMalformed"));

        using var http = new HttpClient(LarisVMS.Infrastructure.Security.OutboundGuard.NoLocalHandler());
        var payload = BuildPayload(ruleName, message, DateTime.UtcNow, details);
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        var response = await http.PostAsync(config.Url, content, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>The JSON body. rule, message and firedAtUtc are always there (what receivers set up
    /// before the details were added rely on them); the rest only when the alert has them.</summary>
    internal static string BuildPayload(string ruleName, string message, DateTime firedAtUtc, AlertEventDetails? details) =>
        JsonSerializer.Serialize(new Payload(
            ruleName, message, firedAtUtc,
            details?.Condition.ToString(),
            details?.CameraId, details?.CameraName,
            details?.NodeId, details?.NodeName,
            details?.Category, details?.Label, details?.SpanId, details?.StartedUtc,
            details?.SpeedKph is { } kph ? Math.Round(kph, 1) : null,
            details?.SnapshotPath), PayloadJson);

    private sealed record Payload(
        [property: JsonPropertyName("rule")] string Rule,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("firedAtUtc")] DateTime FiredAtUtc,
        [property: JsonPropertyName("event")] string? Event,
        [property: JsonPropertyName("cameraId")] Guid? CameraId,
        [property: JsonPropertyName("cameraName")] string? CameraName,
        [property: JsonPropertyName("nodeId")] Guid? NodeId,
        [property: JsonPropertyName("nodeName")] string? NodeName,
        [property: JsonPropertyName("category")] string? Category,
        [property: JsonPropertyName("label")] string? Label,
        [property: JsonPropertyName("spanId")] long? SpanId,
        [property: JsonPropertyName("startedUtc")] DateTime? StartedUtc,
        [property: JsonPropertyName("speedKph")] double? SpeedKph,
        [property: JsonPropertyName("snapshotPath")] string? SnapshotPath);
}
