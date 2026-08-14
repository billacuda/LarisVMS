using Microsoft.EntityFrameworkCore;
using NidusVMS.Core.Entities;
using NidusVMS.Core.Enums;
using NidusVMS.Core.Interfaces;
using NidusVMS.Infrastructure.Data;

namespace NidusVMS.Infrastructure.Services;

/// <summary>Approval-queue / node-download side of recorder-node auto-update — see
/// INodeBuildService's own doc comment for why registration itself (file placement + the initial
/// Pending row) lives in deploy.ps1 instead of here.</summary>
public class NodeBuildService(ApplicationDbContext db) : INodeBuildService
{
    /// <summary>Deliberately outside the IIS site directory entirely — same %ProgramData%\NidusVMS
    /// root as node.config's DPAPI store (NodeConfigStore) and the data-protection key ring
    /// (Program.cs), just its own subfolder. deploy.ps1's robocopy /MIR only ever touches
    /// $DestinationPath, so a folder that never lives under it needs no /XD exclusion entry — see
    /// deploy.ps1's own storage-root guard for why a registered build living *inside* the site
    /// directory would be actively dangerous (the next deploy would silently delete it). Mirrored in
    /// deploy.ps1's own node-build-registration step, which writes directly into the same folder —
    /// keep the two in sync if this ever changes.</summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NidusVMS", "node-builds");

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

    public Task<NodeBuildVersion> ApproveAsync(Guid buildId, string approvedBy, CancellationToken ct = default)
        => SetStatusAsync(buildId, NodeBuildStatus.Approved, approvedBy, ct);

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
