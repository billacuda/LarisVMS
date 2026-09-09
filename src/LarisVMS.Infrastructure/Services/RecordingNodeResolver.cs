using LarisVMS.Core.Enums;

namespace LarisVMS.Infrastructure.Services;

/// <summary>
/// Failover plan phase 3: the pure "which node should be recording this camera right now" decision,
/// driven entirely off persisted <see cref="NodeFailoverState"/> (written by
/// <c>RecordingFailoverService</c>) — never a live health check. Shared by
/// <c>NodeService.GetConfigAsync</c> (so a down node's cameras drop from its own config and appear in
/// its backup's) and the media-routing resolver (so live/playback of a failed-over camera follows it
/// to the backup). Split out so the branch table is unit-testable with no database.
/// </summary>
public static class RecordingNodeResolver
{
    /// <param name="primaryNodeId">The camera's configured owning node (<c>Camera.NodeId</c>).</param>
    /// <param name="cameraBackupOverride"><c>Camera.BackupNodeIdOverride</c>, or null.</param>
    /// <param name="failoverStateOf">Every node's current <see cref="NodeFailoverState"/> — a node not
    /// present is treated as <see cref="NodeFailoverState.Normal"/>.</param>
    /// <param name="defaultBackupOf">Each node's <c>Node.BackupNodeId</c> — absent/null means no
    /// default backup.</param>
    /// <returns>The node id that should record this camera, or null when the camera has no owning
    /// node at all. When the primary is down but no healthy backup resolves, the primary is returned
    /// (its footage — and the decision about whether it is genuinely recording — stays with it).</returns>
    public static Guid? Resolve(
        Guid? primaryNodeId,
        Guid? cameraBackupOverride,
        IReadOnlyDictionary<Guid, NodeFailoverState> failoverStateOf,
        IReadOnlyDictionary<Guid, Guid?> defaultBackupOf)
    {
        if (primaryNodeId is not { } primary) return null;

        if (StateOf(failoverStateOf, primary) != NodeFailoverState.FailedOverAway)
            return primary;

        var backup = cameraBackupOverride ?? (defaultBackupOf.TryGetValue(primary, out var b) ? b : null);
        if (backup is not { } backupId || backupId == primary)
            return primary; // nowhere to fail over to

        // A backup that is itself failed-over is no better than the primary.
        return StateOf(failoverStateOf, backupId) == NodeFailoverState.FailedOverAway ? primary : backupId;
    }

    private static NodeFailoverState StateOf(IReadOnlyDictionary<Guid, NodeFailoverState> map, Guid id)
        => map.TryGetValue(id, out var s) ? s : NodeFailoverState.Normal;
}
