using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Infrastructure.Alerts;

/// <summary>Mirrors EmailProviderFactory — dictionary-maps every DI-registered IAlertChannelSender by
/// its AlertChannel.</summary>
public class AlertChannelSenderFactory(IEnumerable<IAlertChannelSender> senders)
{
    private readonly IReadOnlyDictionary<AlertChannel, IAlertChannelSender> _map =
        senders.ToDictionary(s => s.Channel);

    public IAlertChannelSender Get(AlertChannel channel) =>
        _map.TryGetValue(channel, out var sender)
            ? sender
            : throw new InvalidOperationException($"No IAlertChannelSender is registered for '{channel}'.");
}
