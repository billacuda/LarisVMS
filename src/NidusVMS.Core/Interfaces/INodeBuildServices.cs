using NidusVMS.Core.Entities;

namespace NidusVMS.Core.Interfaces;

/// <summary>Admin-upload / node-download side of recorder-node auto-update. Direct port of dploid's
/// AgentVersionsController + its binary-store helper, collapsed into one service (this codebase's own
/// convention — see ICameraService/CameraService — is a Core interface + Infrastructure implementation
/// per feature area, not a separate static file-store class alongside it).</summary>
public interface INodeBuildService
{
    /// <summary>Streams content to this server's own node-builds storage folder while computing its
    /// SHA-256 and size, then inserts the NodeBuildVersion row. fileName is only used to preserve the
    /// original extension on disk — the stored path is keyed by the new row's own Id, not the
    /// uploaded name, so two uploads can never collide.</summary>
    Task<NodeBuildVersion> UploadAsync(Stream content, string fileName, string version, string platform,
        string? notes, CancellationToken ct = default);

    /// <summary>The newest uploaded build for this platform (by UploadedAt), or null if none has ever
    /// been uploaded — what the heartbeat handler compares a checking-in node's own version against.</summary>
    Task<NodeBuildVersion?> GetLatestForPlatformAsync(string platform, CancellationToken ct = default);

    /// <summary>Every uploaded build, newest first — the admin list page.</summary>
    Task<List<NodeBuildVersion>> ListAsync(CancellationToken ct = default);

    /// <summary>The row for one build, for the download endpoint to resolve a FilePath from — null if
    /// buildId doesn't exist.</summary>
    Task<NodeBuildVersion?> GetDownloadInfoAsync(Guid buildId, CancellationToken ct = default);
}
