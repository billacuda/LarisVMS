using System.Text.Json;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Infrastructure.Alerts.Channels;

/// <summary>Classic Office 365 Connector incoming-webhook format — a JSON POST with a "text" field.
/// Microsoft is retiring Connector webhooks in favor of Workflows (Power Automate), which expect an
/// Adaptive Card payload instead of this simple shape; a webhook URL created through the newer
/// Workflows flow needs a different payload than this and isn't supported by this first pass —
/// verify which kind your Teams webhook is before relying on it.</summary>
public class TeamsAlertChannelSender : IAlertChannelSender
{
    public AlertChannel Channel => AlertChannel.Teams;

    public async Task SendAsync(string? configJson, string ruleName, string message, CancellationToken ct = default)
    {
        var config = string.IsNullOrWhiteSpace(configJson)
            ? throw new InvalidOperationException("Teams delivery configuration is missing.")
            : JsonSerializer.Deserialize<TeamsDeliveryConfig>(configJson)
                ?? throw new InvalidOperationException("Teams delivery configuration is malformed.");

        using var http = new HttpClient();
        var payload = JsonSerializer.Serialize(new { text = $"**LarisVMS alert: {ruleName}**\n\n{message}" });
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        var response = await http.PostAsync(config.WebhookUrl, content, ct);
        response.EnsureSuccessStatusCode();
    }
}
