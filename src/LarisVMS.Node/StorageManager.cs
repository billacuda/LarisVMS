using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LarisVMS.Core.Dtos;

namespace LarisVMS.Node;

/// <summary>
/// Enforces storage limits on this node's own recordings: age-based retention, then per-camera
/// quota, then a global watermark backstop — a segment past its retention window is deleted
/// regardless of quota/watermark state; quota then caps one camera's footprint before the shared
/// watermark pass has to consider it; watermark is the last-resort "the volume is nearly full
/// regardless of what settings say" pass across every camera on this node. Deletion isn't
/// time-critical the way starting/stopping ffmpeg is, so this polls independently of NodeWorker's
/// reconcile loop on its own (slower) cadence rather than competing with recording for attention.
///
/// Purely filesystem-driven: it never touches the database directly (LarisVMS.Node has no DB
/// connection — see the control-plane design in the architecture plan), it deletes files under the
/// storage root and reports the paths it removed back to the web via
/// <c>POST /api/nodes/segments/delete</c>, which is the only side that deletes the matching
/// <c>Segment</c> rows. On a slower cadence it also reconciles: fetches every path the web tier
/// thinks this node still owns (<c>GET /api/nodes/segments/paths</c>) and reports any that are no
/// longer on disk the same way, catching rows a past eviction's deletion report never confirmed.
/// </summary>
public class StorageManager(NodeApiClient api, string fallbackStorageRoot, ILogger<StorageManager> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    // Deliberately much slower than SweepInterval: this asks the web tier for every FilePath it
    // thinks this node still owns (thousands of rows on a busy install) and checks each one against
    // disk, so it's real DB + network load for a check that only ever finds something in the wake of
    // an actual reporting failure — no reason to pay that cost on the same 5-minute cadence eviction
    // needs for freshly-completed segments.
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromHours(1);
    private DateTime? _lastReconciledAtUtc;

    // Files this deleted from disk but hasn't yet successfully told the web tier about — carried
    // over to the next sweep's report attempt on failure. Without this, a deletion report that
    // fails (a network blip, the web tier restarting mid-sweep) permanently orphans the Segment
    // row: the file is already gone from disk by the time the report is attempted, so a later
    // sweep's EnumerateEvictable can never rediscover it to try again. Every other node→web report
    // in this codebase (segments, stream info, motion spans) already re-queues on failure; this one
    // didn't. Plain List, not a ConcurrentQueue: SweepAsync only ever runs from this single
    // BackgroundService's own sequential loop, never concurrently with itself.
    private readonly List<string> _pendingDeletionReports = [];

    // Never touch a file this fresh — the safety margin against evicting the segment ffmpeg might
    // still be writing (default segment length is 60s; this is generous headroom against clock skew
    // and a slow SMB write finishing late).
    private static readonly TimeSpan MinAge = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Storage sweep failed — will retry next cycle.");
            }

            try { await Task.Delay(SweepInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var config = await api.GetConfigAsync(ct);
        var storageRoot = string.IsNullOrWhiteSpace(config.StorageRootPath) ? fallbackStorageRoot : config.StorageRootPath;
        if (!Directory.Exists(storageRoot)) return;

        var now = DateTime.UtcNow;
        var deletedPaths = new List<string>();

        foreach (var camera in config.Cameras)
        {
            var cameraDir = Path.Combine(storageRoot, $"cam-{camera.CameraId}", "main");
            if (!Directory.Exists(cameraDir)) continue;

            var files = EnumerateEvictable(cameraDir, now);

            // Retention sweep: unconditional age cutoff. 0 or negative RetentionDays is an explicit
            // "keep forever" choice, not "unset" (unset falls through to NodeService's compiled-in
            // 30-day default before this DTO is ever built).
            foreach (var f in SelectRetentionEvictions(files, now, camera.RetentionDays))
            {
                if (TryDelete(f.FullName)) { deletedPaths.Add(f.FullName); files.Remove(f); }
            }

            // Per-camera quota: oldest-first until back under the cap.
            foreach (var f in SelectQuotaEvictions(files, camera.QuotaBytes))
            {
                if (TryDelete(f.FullName)) { deletedPaths.Add(f.FullName); files.Remove(f); }
            }

            PruneEmptyDirectories(cameraDir);
        }

        await ApplyWatermarkAsync(config, storageRoot, now, deletedPaths, ct);

        // Export output files (and any stray concat list file ExportRunner didn't get to clean up
        // after a crash) — a sibling of the cam-{id}/ folders above, deliberately never walked by
        // the per-camera loop, so it needs its own pass. Age-only, no quota/watermark interaction:
        // exports are a small, occasional side product, not part of the retention/quota budget any
        // camera's continuous recording competes for. Not reported to /segments/delete — these were
        // never Segment rows, so there's nothing for the web tier to reconcile.
        SweepExportsDirectory(storageRoot, now);

        if (_lastReconciledAtUtc is null || now - _lastReconciledAtUtc >= ReconcileInterval)
        {
            // Only stamped on success — a failed fetch (network blip, web tier restarting) should
            // retry on the next 5-minute sweep, not wait a full extra hour for the next scheduled one.
            if (await ReconcileAsync(deletedPaths, ct)) _lastReconciledAtUtc = now;
        }

        // Anything left over from a prior sweep's failed report rides along with this sweep's own
        // batch — one retry attempt covers both, rather than needing its own separate call.
        deletedPaths.AddRange(_pendingDeletionReports);
        _pendingDeletionReports.Clear();

        if (deletedPaths.Count > 0)
        {
            logger.LogInformation("Evicted {Count} segment(s) for storage limits.", deletedPaths.Count);
            try
            {
                await api.DeleteSegmentsAsync(deletedPaths, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The files are already gone from disk regardless of whether this succeeds — only
                // the *report* is being retried, not the deletion itself.
                _pendingDeletionReports.AddRange(deletedPaths);
                logger.LogWarning(ex, "Failed to report {Count} deleted segment(s) to the server — will retry next sweep.", deletedPaths.Count);
            }
        }
    }

    private async Task ApplyWatermarkAsync(NodeConfigResponse config, string storageRoot,
        DateTime now, List<string> deletedPaths, CancellationToken ct)
    {
        var usage = DiskSpace.TryGetUsage(storageRoot);
        if (usage is not { } u || u.TotalBytes <= 0) return;
        if (100.0 * (u.TotalBytes - u.FreeBytes) / u.TotalBytes <= config.WatermarkPercent) return;

        // Global oldest-first across every camera on this node — retention/quota already ran, so
        // whatever's left here is "in policy" but the volume is full anyway; age is the only fair
        // tiebreaker across cameras with different quotas/retention.
        var candidates = config.Cameras
            .Select(c => Path.Combine(storageRoot, $"cam-{c.CameraId}", "main"))
            .Where(Directory.Exists)
            .SelectMany(dir => EnumerateEvictable(dir, now))
            .OrderBy(f => f.LastWriteTimeUtc);

        foreach (var f in candidates)
        {
            var current = DiskSpace.TryGetUsage(storageRoot);
            if (current is null) break;
            if (100.0 * (current.Value.TotalBytes - current.Value.FreeBytes) / current.Value.TotalBytes <= config.WatermarkPercent) break;

            if (TryDelete(f.FullName)) deletedPaths.Add(f.FullName);
        }

        await Task.CompletedTask;
    }

    /// <summary>Finds and reports Segment rows whose file no longer exists on this node's disk —
    /// most commonly one this StorageManager itself deleted at some point in the past whose deletion
    /// report never landed (a gap that existed for every node→web report in this app until it was
    /// fixed for this one specifically; the fix stops new orphans but can't retroactively clean up
    /// rows stranded before it existed). Reported through the same path as a normal eviction: from
    /// the web tier's side, "this file is gone" means the same thing regardless of which sweep
    /// noticed it. Returns false (and reports nothing) on a fetch failure so the caller can retry
    /// sooner than the next scheduled reconcile interval.</summary>
    private async Task<bool> ReconcileAsync(List<string> deletedPaths, CancellationToken ct)
    {
        List<string> knownPaths;
        try
        {
            knownPaths = await api.GetSegmentFilePathsAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Reconciliation sweep could not fetch this node's known segment paths — will retry next sweep.");
            return false;
        }

        var missing = SelectMissingPaths(knownPaths);
        if (missing.Count > 0)
        {
            logger.LogInformation(
                "Reconciliation sweep found {Count} segment row(s) (of {Total} checked) pointing at files no longer on disk — reporting for cleanup.",
                missing.Count, knownPaths.Count);
            deletedPaths.AddRange(missing);
        }

        return true;
    }

    /// <summary>Pure selection extracted from ReconcileAsync so it's testable against a real temp
    /// directory the same way EnumerateEvictable is, without a network round trip.</summary>
    internal static List<string> SelectMissingPaths(IEnumerable<string> knownPaths)
        => knownPaths.Where(p => !File.Exists(p)).ToList();

    internal static List<FileInfo> EnumerateEvictable(string cameraDir, DateTime now)
        => Directory.EnumerateFiles(cameraDir, "*.mp4", SearchOption.AllDirectories)
            .Select(p => new FileInfo(p))
            .Where(f => now - f.LastWriteTimeUtc > MinAge)
            .ToList();

    /// <summary>Files past the age cutoff — a pure decision extracted from the sweep so it's testable
    /// without touching a real (or fake) filesystem. 0/negative retentionDays means "keep forever".</summary>
    internal static List<FileInfo> SelectRetentionEvictions(List<FileInfo> files, DateTime now, int? retentionDays)
    {
        if (retentionDays is not > 0) return [];
        var cutoff = now.AddDays(-retentionDays.Value);
        return files.Where(f => f.LastWriteTimeUtc < cutoff).ToList();
    }

    /// <summary>Oldest-first files to delete until the camera's total footprint is back at or under
    /// quotaBytes. Null/non-positive quotaBytes means "no quota — shares the pool".</summary>
    internal static List<FileInfo> SelectQuotaEvictions(List<FileInfo> files, long? quotaBytes)
    {
        if (quotaBytes is not > 0) return [];

        var result = new List<FileInfo>();
        var total = files.Sum(f => f.Length);
        foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc))
        {
            if (total <= quotaBytes) break;
            result.Add(f);
            total -= f.Length;
        }

        return result;
    }

    // Generous relative to how long a browser download realistically takes, but still bounded —
    // this is finished export output sitting on local disk, not something with its own retention
    // policy anywhere else, so it has to expire on its own eventually.
    private static readonly TimeSpan ExportRetention = TimeSpan.FromDays(7);

    private void SweepExportsDirectory(string storageRoot, DateTime now)
    {
        var exportsDir = Path.Combine(storageRoot, "exports");
        if (!Directory.Exists(exportsDir)) return;

        // Flat, not nested by camera/date the way cam-{id}/main is — output file names are already
        // unique (camera name/id + from/to timestamps), so there's nothing to organize by.
        foreach (var path in Directory.EnumerateFiles(exportsDir, "*", SearchOption.TopDirectoryOnly))
        {
            FileInfo info;
            try { info = new FileInfo(path); }
            catch (IOException) { continue; }

            if (now - info.LastWriteTimeUtc > ExportRetention) TryDelete(path);
        }
    }

    private bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete {Path} — will retry next sweep.", path);
            return false;
        }
    }

    internal static void PruneEmptyDirectories(string root)
    {
        // Nested date/hour folders (cam-<id>/main/yyyy/MM/dd/HH/) leave an empty leaf behind once
        // all its segments are evicted; ordering by path length longest-first prunes hour folders
        // before their parent day/month/year folders in the same pass, rather than needing several
        // sweeps to unwind a fully-evicted tree one level at a time.
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .OrderByDescending(d => d.Length))
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (IOException)
            {
                // In use or already removed by a concurrent prune — next sweep will retry.
            }
        }
    }
}
