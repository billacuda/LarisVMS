using LarisVMS.Core.Entities;

namespace LarisVMS.Core.Interfaces;

/// <summary>Approval-queue / node-download side of recorder-node auto-update. Registration itself
/// (file placement + the initial Pending row) happens outside this service now — deploy.ps1 writes
/// straight to the node-builds folder and the database, the same way it already reads/writes
/// Settings for the storage-root guard — there is no browser upload path anymore (IIS's own
/// requestFiltering content-length limit made a several-hundred-MB self-contained exe upload through
/// the browser a recurring 413, and deploy.ps1 already runs locally on the server with everything it
/// needs). This interface only covers what still needs to run in-process: the approve/reject queue
/// workflow and the node-facing download lookup.</summary>
public interface INodeBuildService
{
    /// <summary>The newest *approved* build for this platform (by UploadedAt), or null if none has
    /// ever been approved — what the heartbeat handler compares a checking-in node's own version
    /// against. Pending/Rejected rows are never offered to a node.</summary>
    Task<NodeBuildVersion?> GetLatestForPlatformAsync(string platform, CancellationToken ct = default);

    /// <summary>Every registered build regardless of status, newest first — the admin queue page.</summary>
    Task<List<NodeBuildVersion>> ListAsync(CancellationToken ct = default);

    /// <summary>The row for one build, for the download endpoint to resolve a FilePath from — null if
    /// buildId doesn't exist.</summary>
    Task<NodeBuildVersion?> GetDownloadInfoAsync(Guid buildId, CancellationToken ct = default);

    /// <summary>Moves a Pending build to Approved, making it eligible for GetLatestForPlatformAsync.
    /// Throws if buildId doesn't exist. A no-op re-stamp (ApprovedAt/ApprovedBy overwritten) if it's
    /// already Approved — there's no workflow reason to forbid re-approving.</summary>
    Task<NodeBuildVersion> ApproveAsync(Guid buildId, string approvedBy, CancellationToken ct = default);

    /// <summary>Moves a build to Rejected, permanently excluding it from GetLatestForPlatformAsync —
    /// the row (and its file) is kept, not deleted, purely as an audit trail of what was built and
    /// turned down. Throws if buildId doesn't exist.</summary>
    Task<NodeBuildVersion> RejectAsync(Guid buildId, string approvedBy, CancellationToken ct = default);
}
