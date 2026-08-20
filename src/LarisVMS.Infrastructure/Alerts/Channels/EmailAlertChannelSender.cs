using System.Text.Json;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Infrastructure.Alerts.Channels;

/// <summary>Routes through the app's own outbound EmailSettings (SMTP/Graph/Gmail, whichever is
/// configured) rather than needing its own provider config — an alert email is exactly what
/// IEmailService already exists to send.</summary>
public class EmailAlertChannelSender(IEmailService emailService) : IAlertChannelSender
{
    public AlertChannel Channel => AlertChannel.Email;

    public async Task SendAsync(string? configJson, string ruleName, string message, CancellationToken ct = default)
    {
        var config = Deserialize(configJson);
        await emailService.SendAsync(config.To, $"LarisVMS alert: {ruleName}", $"<p>{message}</p>", message, ct);
    }

    private static EmailDeliveryConfig Deserialize(string? configJson) =>
        string.IsNullOrWhiteSpace(configJson) ? throw new InvalidOperationException("Email delivery configuration is missing.")
            : JsonSerializer.Deserialize<EmailDeliveryConfig>(configJson)
                ?? throw new InvalidOperationException("Email delivery configuration is malformed.");
}
