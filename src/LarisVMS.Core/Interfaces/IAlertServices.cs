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
}
