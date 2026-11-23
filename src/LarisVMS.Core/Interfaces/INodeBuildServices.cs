using LarisVMS.Core.Entities;

namespace LarisVMS.Core.Interfaces;

/// <summary>Approval-queue / node-download side of recorder-node auto-update. Registration itself
/// (file placement + the initial Pending row) happens in BundledBuildRegistrar at startup, from the
/// builds the Web package carries; there is no browser upload path (a several-hundred-MB
/// self-contained exe through the browser was a recurring 413). This interface only covers what still needs to run in-process: the approve/reject queue
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

    /// <summary>Moves a Pending build to Approved, making it eligible for GetLatestForPlatformAsync,
    /// then supersedes any older Pending builds of the same platform. Throws if buildId doesn't exist,
    /// or if the build is Superseded or Rejected. A no-op re-stamp (ApprovedAt/ApprovedBy overwritten)
    /// if it's already Approved — there's no workflow reason to forbid re-approving.</summary>
    Task<NodeBuildVersion> ApproveAsync(Guid buildId, string approvedBy, CancellationToken ct = default);

    /// <summary>Moves a build to Rejected, permanently excluding it from GetLatestForPlatformAsync —
    /// the row (and its file) is kept, not deleted, purely as an audit trail of what was built and
    /// turned down. Throws if buildId doesn't exist.</summary>
    Task<NodeBuildVersion> RejectAsync(Guid buildId, string approvedBy, CancellationToken ct = default);

    /// <summary>Keeps only the newest Pending build per platform awaiting approval, and only while
    /// it's newer than the newest Approved one; every other Pending build becomes Superseded.
    /// Returns how many were superseded. Also run automatically after an approval and after the
    /// bundled builds are registered.</summary>
    Task<int> SupersedeOutdatedPendingAsync(CancellationToken ct = default);

    /// <summary>Deletes every build older than its platform's approved build — whatever its status —
    /// with its stored files, plus any file in the builds folder no build refers to. A platform with
    /// no approved build is left alone, and so is anything newer than the approved build (a build
    /// waiting for approval). The approved build itself is always kept: nodes still download it.</summary>
    Task<BuildPurgeResult> PurgeOldBuildsAsync(CancellationToken ct = default);
}

/// <summary>What <see cref="INodeBuildService.PurgeOldBuildsAsync"/> removed.</summary>
public record BuildPurgeResult(int Builds, int Files, long Bytes);

/// <summary>Registers the node and media-proxy builds shipped inside the web install (its
/// <c>packages\</c> folder, filled by the Web MSI or install-web.ps1) as Pending on the Nodes page,
/// and seeds the CUDA provider library NVIDIA nodes download. Run once at startup; idempotent — a
/// version already registered for its platform is skipped.</summary>
public interface IBundledBuildRegistrar
{
    Task RegisterAsync(string packagesRoot, CancellationToken ct = default);
}
