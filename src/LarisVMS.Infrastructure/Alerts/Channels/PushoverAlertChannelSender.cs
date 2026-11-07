using System.Text.Json;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Core.Localization;

namespace LarisVMS.Infrastructure.Alerts.Channels;

/// <summary>Pushover's messages API — a form-encoded POST, https://pushover.net/api.</summary>
public class PushoverAlertChannelSender : IAlertChannelSender
{
    public AlertChannel Channel => AlertChannel.Pushover;

    public async Task SendAsync(string? configJson, string ruleName, string message, CancellationToken ct = default)
    {
        var config = string.IsNullOrWhiteSpace(configJson)
            ? throw new InvalidOperationException(Loc.T("server.pushoverAlertChannel.pushoverDeliveryConfigurationIsMissing"))
            : JsonSerializer.Deserialize<PushoverDeliveryConfig>(configJson)
                ?? throw new InvalidOperationException(Loc.T("server.pushoverAlertChannel.pushoverDeliveryConfigurationIsMalformed"));

        using var http = new HttpClient(LarisVMS.Infrastructure.Security.OutboundGuard.NoLocalHandler());
        var response = await http.PostAsync("https://api.pushover.net/1/messages.json",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = config.AppToken,
                ["user"] = config.UserKey,
                ["title"] = $"LarisVMS alert: {ruleName}",
                ["message"] = message
            }), ct);
        response.EnsureSuccessStatusCode();
    }
}
