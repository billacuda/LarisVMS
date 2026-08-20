using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Alerts;

namespace LarisVMS.Tests;

public class AlertChannelSenderFactoryTests
{
    private sealed class FakeSender(AlertChannel channel) : IAlertChannelSender
    {
        public AlertChannel Channel => channel;
        public Task SendAsync(string? configJson, string ruleName, string message, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public void ResolvesTheSenderMatchingTheRequestedChannel()
    {
        var factory = new AlertChannelSenderFactory([new FakeSender(AlertChannel.Email), new FakeSender(AlertChannel.Slack)]);

        Assert.Equal(AlertChannel.Email, factory.Get(AlertChannel.Email).Channel);
        Assert.Equal(AlertChannel.Slack, factory.Get(AlertChannel.Slack).Channel);
    }

    [Fact]
    public void ThrowsForAChannelNothingIsRegisteredFor()
    {
        var factory = new AlertChannelSenderFactory([new FakeSender(AlertChannel.Email)]);

        Assert.Throws<InvalidOperationException>(() => factory.Get(AlertChannel.Webhook));
    }
}
