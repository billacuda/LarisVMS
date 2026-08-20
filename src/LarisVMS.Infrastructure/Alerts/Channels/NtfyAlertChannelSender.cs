using System.Text.Json;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Infrastructure.Alerts.Channels;

/// <summary>ntfy's publish API: a plain-text POST body to {server}/{topic}, with the title carried in
/// a header rather than the body — https://docs.ntfy.sh/publish/. No auth support in this first pass
/// (a public/self-hosted topic with no access control); ntfy's token/Basic-auth options are a
/// straightforward addition to NtfyDeliveryConfig later if needed.</summary>
public class NtfyAlertChannelSender : IAlertChannelSender
{
    private const string DefaultServerUrl = "https://ntfy.sh";

    public AlertChannel Channel => AlertChannel.Ntfy;

    public async Task SendAsync(string? configJson, string ruleName, string message, CancellationToken ct = default)
    {
        var config = string.IsNullOrWhiteSpace(configJson)
            ? throw new InvalidOperationException("ntfy delivery configuration is missing.")
            : JsonSerializer.Deserialize<NtfyDeliveryConfig>(configJson)
                ?? throw new InvalidOperationException("ntfy delivery configuration is malformed.");

        var serverUrl = string.IsNullOrWhiteSpace(config.ServerUrl) ? DefaultServerUrl : config.ServerUrl.TrimEnd('/');

        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl}/{config.Topic}")
        {
            Content = new StringContent(message, System.Text.Encoding.UTF8)
        };
        request.Headers.Add("Title", $"LarisVMS alert: {ruleName}");
        var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }
}
