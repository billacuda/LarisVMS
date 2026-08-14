using NidusVMS.Core;

namespace NidusVMS.Tests;

/// <summary>Covers NodeVersionComparer.IsNewer — the recorder-node auto-update gate in
/// NidusVMS.Web/Program.cs's heartbeat handler (compares the newest uploaded NodeBuildVersion against
/// the checking-in node's own reported version). Mirrors dploid's AgentController.IsNewerVersion,
/// including its null-current fail-open behavior.</summary>
public class NodeVersionComparerTests
{
    [Theory]
    [InlineData("0.47.0", "0.46.0")]
    [InlineData("1.0.0", "0.99.99")]
    [InlineData("0.46.1", "0.46.0")]
    public void NewerVersionIsDetectedAsNewer(string latest, string current)
    {
        Assert.True(NodeVersionComparer.IsNewer(latest, current));
    }

    [Theory]
    [InlineData("0.46.0", "0.46.0")]
    [InlineData("0.46.0", "0.47.0")]
    [InlineData("0.1.0", "1.0.0")]
    public void SameOrOlderVersionIsNotNewer(string latest, string current)
    {
        Assert.False(NodeVersionComparer.IsNewer(latest, current));
    }

    [Fact]
    public void NullOrEmptyCurrentVersionAlwaysCountsAsNewer()
    {
        // A fresh registration, or a node whose first heartbeat hasn't landed yet — nothing to
        // compare against, so any uploaded build is offered rather than the node getting stuck.
        Assert.True(NodeVersionComparer.IsNewer("0.1.0", null));
        Assert.True(NodeVersionComparer.IsNewer("0.1.0", ""));
    }

    [Theory]
    [InlineData("not-a-version", "0.46.0")]
    [InlineData("0.46.0", "not-a-version")]
    public void UnparsableVersionStringsAreNotNewer(string latest, string current)
    {
        // Fails closed — an unparsable version on either side must never be treated as "newer",
        // which would otherwise push an update based on a comparison that couldn't actually be made.
        Assert.False(NodeVersionComparer.IsNewer(latest, current));
    }
}
