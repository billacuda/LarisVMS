using LarisVMS.Core.Enums;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>Failover plan phase 3: the pure "which node records this camera now" branch table.</summary>
public class RecordingNodeResolverTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid C = Guid.NewGuid();

    private static Guid? Resolve(Guid? primary, Guid? camOverride,
        (Guid id, NodeFailoverState state)[] states, (Guid id, Guid? backup)[] backups)
        => RecordingNodeResolver.Resolve(primary, camOverride,
            states.ToDictionary(x => x.id, x => x.state),
            backups.ToDictionary(x => x.id, x => x.backup));

    [Fact]
    public void HealthyPrimary_RecordsItsOwnCameras()
        => Assert.Equal(A, Resolve(A, null,
            [(A, NodeFailoverState.Normal)], [(A, B)]));

    [Fact]
    public void HostingPrimary_StillRecordsItsOwnCameras()
        => Assert.Equal(A, Resolve(A, null,
            [(A, NodeFailoverState.HostingFailover)], [(A, B)]));

    [Fact]
    public void DownPrimary_WithHealthyDefaultBackup_MovesToBackup()
        => Assert.Equal(B, Resolve(A, null,
            [(A, NodeFailoverState.FailedOverAway), (B, NodeFailoverState.Normal)], [(A, B)]));

    [Fact]
    public void DownPrimary_PerCameraOverrideWins()
        => Assert.Equal(C, Resolve(A, C,
            [(A, NodeFailoverState.FailedOverAway), (B, NodeFailoverState.Normal), (C, NodeFailoverState.Normal)],
            [(A, B)]));

    [Fact]
    public void DownPrimary_NoBackupConfigured_StaysWithPrimary()
        => Assert.Equal(A, Resolve(A, null,
            [(A, NodeFailoverState.FailedOverAway)], [(A, (Guid?)null)]));

    [Fact]
    public void DownPrimary_BackupAlsoDown_StaysWithPrimary()
        => Assert.Equal(A, Resolve(A, null,
            [(A, NodeFailoverState.FailedOverAway), (B, NodeFailoverState.FailedOverAway)], [(A, B)]));

    [Fact]
    public void DownPrimary_BackupIsItself_StaysWithPrimary()
        => Assert.Equal(A, Resolve(A, null,
            [(A, NodeFailoverState.FailedOverAway)], [(A, A)]));

    [Fact]
    public void NoOwningNode_ResolvesToNull()
        => Assert.Null(Resolve(null, B, [(B, NodeFailoverState.Normal)], []));
}
