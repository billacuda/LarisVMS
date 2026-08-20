using System.Text.Json;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Infrastructure.Alerts.Channels;

/// <summary>Generic outbound webhook — POSTs a small JSON envelope so any receiver (a custom
/// automation, a monitoring system's inbound-webhook endpoint) gets rule name, message, and a
/// firing timestamp without needing to parse a channel-specific payload shape.</summary>
public class WebhookAlertChannelSender : IAlertChannelSender
{
    public AlertChannel Channel => AlertChannel.Webhook;

    public async Task SendAsync(string? configJson, string ruleName, string message, CancellationToken ct = default)
    {
        var config = string.IsNullOrWhiteSpace(configJson)
            ? throw new InvalidOperationException("Webhook delivery configuration is missing.")
            : JsonSerializer.Deserialize<WebhookDeliveryConfig>(configJson)
                ?? throw new InvalidOperationException("Webhook delivery configuration is malformed.");

        using var http = new HttpClient();
        var payload = JsonSerializer.Serialize(new { rule = ruleName, message, firedAtUtc = DateTime.UtcNow });
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        var response = await http.PostAsync(config.Url, content, ct);
        response.EnsureSuccessStatusCode();
    }
}
