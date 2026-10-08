using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

/// <summary>Approval-queue / node-download side of recorder-node auto-update — see
/// INodeBuildService's own doc comment for why registration itself (file placement + the initial
/// Pending row) lives in deploy.ps1 instead of here.</summary>
public class NodeBuildService(ApplicationDbContext db) : INodeBuildService
{
    /// <summary>Deliberately outside the IIS site directory entirely — same %ProgramData%\LarisVMS
    /// root as node.config's DPAPI store (NodeConfigStore) and the data-protection key ring
    /// (Program.cs), just its own subfolder. deploy.ps1's robocopy /MIR only ever touches
    /// $DestinationPath, so a folder that never lives under it needs no /XD exclusion entry — see
    /// deploy.ps1's own storage-root guard for why a registered build living *inside* the site
    /// directory would be actively dangerous (the next deploy would silently delete it). Mirrored in
    /// deploy.ps1's own node-build-registration step, which writes directly into the same folder —
    /// keep the two in sync if this ever changes.</summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LarisVMS", "node-builds");

    public async Task<NodeBuildVersion?> GetLatestForPlatformAsync(string platform, CancellationToken ct = default)
        => await db.NodeBuildVersions.AsNoTracking()
            .Where(b => b.Platform == platform && b.Status == NodeBuildStatus.Approved)
            .OrderByDescending(b => b.UploadedAt)
            .FirstOrDefaultAsync(ct);

    public async Task<List<NodeBuildVersion>> ListAsync(CancellationToken ct = default)
        => await db.NodeBuildVersions.AsNoTracking()
            .OrderByDescending(b => b.UploadedAt)
            .ToListAsync(ct);

    public async Task<NodeBuildVersion?> GetDownloadInfoAsync(Guid buildId, CancellationToken ct = default)
        => await db.NodeBuildVersions.AsNoTracking().FirstOrDefaultAsync(b => b.Id == buildId, ct);

    public async Task<NodeBuildVersion> ApproveAsync(Guid buildId, string approvedBy, CancellationToken ct = default)
    {
        var current = await db.NodeBuildVersions.AsNoTracking().FirstOrDefaultAsync(b => b.Id == buildId, ct);
        if (current?.Status is NodeBuildStatus.Superseded or NodeBuildStatus.Rejected)
            throw new InvalidOperationException($"Build {current.Version} is {current.Status.ToString().ToLowerInvariant()} and can't be approved.");

        var build = await SetStatusAsync(buildId, NodeBuildStatus.Approved, approvedBy, ct);
        // Anything older still pending for this platform would now only roll nodes back.
        await SupersedeOutdatedPendingAsync(db, ct);
        return build;
    }

    public Task<int> SupersedeOutdatedPendingAsync(CancellationToken ct = default) => SupersedeOutdatedPendingAsync(db, ct);

    /// <summary>Per platform, leaves only the newest Pending build pending — and only while it's newer
    /// than the newest Approved one — and marks the rest Superseded. Versions compare as
    /// System.Version (NodeVersionComparer's ordering), newest registration breaking ties; an
    /// unparseable version sorts lowest. Static so BundledBuildRegistrar can run it on its own
    /// context; deploy.ps1 inserts Pending rows straight into the table, so the admin page runs it
    /// too rather than relying on any one registration path.</summary>
    internal static async Task<int> SupersedeOutdatedPendingAsync(ApplicationDbContext db, CancellationToken ct)
    {
        var candidates = await db.NodeBuildVersions
            .Where(b => b.Status == NodeBuildStatus.Pending || b.Status == NodeBuildStatus.Approved)
            .ToListAsync(ct);

        var superseded = 0;
        foreach (var platform in candidates.GroupBy(b => b.Platform))
        {
            var newestApproved = platform.Where(b => b.Status == NodeBuildStatus.Approved)
                .Select(b => ParseVersion(b.Version)).DefaultIfEmpty(new Version(0, 0)).Max()!;
            var pending = platform.Where(b => b.Status == NodeBuildStatus.Pending)
                .OrderByDescending(b => ParseVersion(b.Version)).ThenByDescending(b => b.UploadedAt).ToList();

            for (var i = 0; i < pending.Count; i++)
            {
                var keep = i == 0 && ParseVersion(pending[i].Version) > newestApproved;
                if (keep) continue;
                pending[i].Status = NodeBuildStatus.Superseded;
                pending[i].ApprovedAt = DateTime.UtcNow;
                pending[i].ApprovedBy = "System";
                superseded++;
            }
        }

        if (superseded > 0) await db.SaveChangesAsync(ct);
        return superseded;
    }

    private static Version ParseVersion(string version) => Version.TryParse(version, out var v) ? v : new Version(0, 0);

    public Task<NodeBuildVersion> RejectAsync(Guid buildId, string approvedBy, CancellationToken ct = default)
        => SetStatusAsync(buildId, NodeBuildStatus.Rejected, approvedBy, ct);

    private async Task<NodeBuildVersion> SetStatusAsync(Guid buildId, NodeBuildStatus status, string approvedBy, CancellationToken ct)
    {
        var build = await db.NodeBuildVersions.FirstOrDefaultAsync(b => b.Id == buildId, ct)
            ?? throw new InvalidOperationException($"Node build {buildId} not found.");
        build.Status = status;
        build.ApprovedAt = DateTime.UtcNow;
        build.ApprovedBy = approvedBy;
        await db.SaveChangesAsync(ct);
        return build;
    }
}
