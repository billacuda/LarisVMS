using System.Text.Json;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Infrastructure.Alerts.Channels;

/// <summary>Slack's incoming-webhook format — a JSON POST with a single "text" field, the
/// lowest-common-denominator payload every incoming webhook accepts regardless of workspace Block Kit
/// settings.</summary>
public class SlackAlertChannelSender : IAlertChannelSender
{
    public AlertChannel Channel => AlertChannel.Slack;

    public async Task SendAsync(string? configJson, string ruleName, string message, CancellationToken ct = default)
    {
        var config = string.IsNullOrWhiteSpace(configJson)
            ? throw new InvalidOperationException("Slack delivery configuration is missing.")
            : JsonSerializer.Deserialize<SlackDeliveryConfig>(configJson)
                ?? throw new InvalidOperationException("Slack delivery configuration is malformed.");

        using var http = new HttpClient();
        var payload = JsonSerializer.Serialize(new { text = $"*LarisVMS alert: {ruleName}*\n{message}" });
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        var response = await http.PostAsync(config.WebhookUrl, content, ct);
        response.EnsureSuccessStatusCode();
    }
}
