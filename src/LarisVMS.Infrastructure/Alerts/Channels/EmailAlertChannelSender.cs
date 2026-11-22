using System.Net;
using System.Text.Json;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Core.Localization;

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
        // The message carries text people typed (camera names, evidence-lock reasons), so it is
        // encoded for the HTML part; otherwise a name could put a link or image into a real alert.
        await emailService.SendAsync(config.To, $"LarisVMS alert: {ruleName}", $"<p>{WebUtility.HtmlEncode(message)}</p>", message, ct);
    }

    private static EmailDeliveryConfig Deserialize(string? configJson) =>
        string.IsNullOrWhiteSpace(configJson) ? throw new InvalidOperationException(Loc.T("server.emailAlertChannel.emailDeliveryConfigurationIsMissing"))
            : JsonSerializer.Deserialize<EmailDeliveryConfig>(configJson)
                ?? throw new InvalidOperationException(Loc.T("server.emailAlertChannel.emailDeliveryConfigurationIsMalformed"));
}
