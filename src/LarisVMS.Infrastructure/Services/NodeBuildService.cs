using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Core.Localization;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

/// <summary>Approval-queue / node-download side of recorder-node auto-update — see
/// INodeBuildService's own doc comment for why registration itself (file placement + the initial
/// Pending row) lives in BundledBuildRegistrar instead of here.</summary>
public class NodeBuildService(ApplicationDbContext db) : INodeBuildService
{
    /// <summary>Deliberately outside the IIS site directory entirely — same %ProgramData%\LarisVMS
    /// root as node.config's DPAPI store (NodeConfigStore) and the data-protection key ring
    /// (Program.cs), just its own subfolder. install-web.ps1's robocopy /MIR only touches the install
    /// folder, so a folder outside it needs no /XD exclusion; a build stored *inside* the install folder
    /// would be silently deleted by the next install.</summary>
    public static string DefaultRoot => Path.Combine(LarisVMS.Core.LarisPaths.DataRoot, "node-builds");

    /// <summary>The folder the builds' files are stored in — overridable for tests. Only files inside
    /// it are ever deleted.</summary>
    public string BuildsRoot { get; init; } = DefaultRoot;

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
    /// context; the admin page runs it too rather than relying on any one registration path.</summary>
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

    /// <summary>The builds <see cref="PurgeOldBuildsAsync"/> would delete: per platform, every build
    /// whose version is older than the newest approved one. Public so the Nodes page can show the
    /// count before anything is deleted.</summary>
    public static List<NodeBuildVersion> Purgeable(IEnumerable<NodeBuildVersion> builds)
    {
        var result = new List<NodeBuildVersion>();
        foreach (var platform in builds.GroupBy(b => b.Platform))
        {
            var approved = platform.Where(b => b.Status == NodeBuildStatus.Approved)
                .OrderByDescending(b => ParseVersion(b.Version)).ThenByDescending(b => b.UploadedAt).FirstOrDefault();
            if (approved is null) continue;
            var current = ParseVersion(approved.Version);
            result.AddRange(platform.Where(b => b.Id != approved.Id && ParseVersion(b.Version) < current));
        }
        return result;
    }

    public async Task<BuildPurgeResult> PurgeOldBuildsAsync(CancellationToken ct = default)
    {
        var all = await db.NodeBuildVersions.ToListAsync(ct);
        var purge = Purgeable(all);
        var (files, bytes) = (0, 0L);

        foreach (var build in purge)
        {
            foreach (var path in new[] { build.FilePath, build.VisionFilePath, build.BackendsFilePath })
            {
                if (TryDeleteStoredFile(path) is { } size) { files++; bytes += size; }
            }
        }
        if (purge.Count > 0)
        {
            db.NodeBuildVersions.RemoveRange(purge);
            await db.SaveChangesAsync(ct);
        }

        // Files left behind by builds removed some other way, or by a registration that failed
        // halfway: anything in the folder no remaining build points at.
        var kept = all.Except(purge)
            .SelectMany(b => new[] { b.FilePath, b.VisionFilePath, b.BackendsFilePath })
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => Path.GetFullPath(p!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(BuildsRoot))
        {
            foreach (var file in Directory.EnumerateFiles(BuildsRoot))
            {
                if (kept.Contains(Path.GetFullPath(file))) continue;
                if (TryDeleteStoredFile(file) is { } size) { files++; bytes += size; }
            }
        }
        return new BuildPurgeResult(purge.Count, files, bytes);
    }

    /// <summary>Deletes a build's file when it's directly inside <see cref="BuildsRoot"/>; returns its
    /// size, or null when it was elsewhere, missing, or couldn't be deleted (a download in progress
    /// holds it open; the next clean-up gets it).</summary>
    private long? TryDeleteStoredFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(BuildsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return null;
            var size = new FileInfo(full).Length;
            File.Delete(full);
            return size;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public Task<NodeBuildVersion> RejectAsync(Guid buildId, string approvedBy, CancellationToken ct = default)
        => SetStatusAsync(buildId, NodeBuildStatus.Rejected, approvedBy, ct);

    private async Task<NodeBuildVersion> SetStatusAsync(Guid buildId, NodeBuildStatus status, string approvedBy, CancellationToken ct)
    {
        var build = await db.NodeBuildVersions.FirstOrDefaultAsync(b => b.Id == buildId, ct)
            ?? throw new InvalidOperationException(Loc.T("server.nodeBuild.nodeBuildNotFound", new { buildId }));
        build.Status = status;
        build.ApprovedAt = DateTime.UtcNow;
        build.ApprovedBy = approvedBy;
        await db.SaveChangesAsync(ct);
        return build;
    }
}
