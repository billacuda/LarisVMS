using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>Covers NodeService.ComputeClockSkewSeconds — the pure arithmetic behind the node
/// clock-skew measurement (M11 NTP/timezone pass), same "isolate the arithmetic from the I/O" shape
/// as TimelineServiceTests' NormalizeToUtc coverage.</summary>
public class NodeServiceTests
{
    [Fact]
    public void SkewIsNullWhenTheNodeDidNotSendASentAtTimestamp()
    {
        Assert.Null(NodeService.ComputeClockSkewSeconds(null, DateTime.UtcNow));
    }

    [Fact]
    public void SkewIsPositiveWhenTheNodesClockIsBehindTheServers()
    {
        var serverNow = new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);
        var nodeSentAt = serverNow.AddSeconds(-90); // node's clock reads 90s earlier

        Assert.Equal(90, NodeService.ComputeClockSkewSeconds(nodeSentAt, serverNow));
    }

    [Fact]
    public void SkewIsNegativeWhenTheNodesClockIsAheadOfTheServers()
    {
        var serverNow = new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);
        var nodeSentAt = serverNow.AddSeconds(45); // node's clock reads 45s later

        Assert.Equal(-45, NodeService.ComputeClockSkewSeconds(nodeSentAt, serverNow));
    }

    [Fact]
    public void SkewIsZeroWhenBothClocksAgree()
    {
        var now = new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(0, NodeService.ComputeClockSkewSeconds(now, now));
    }
}
