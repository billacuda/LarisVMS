using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NidusVMS.Core.Dtos;

namespace NidusVMS.Node;

/// <summary>
/// Enforces storage limits on this node's own recordings: age-based retention, then per-camera
/// quota, then a global watermark backstop — a segment past its retention window is deleted
/// regardless of quota/watermark state; quota then caps one camera's footprint before the shared
/// watermark pass has to consider it; watermark is the last-resort "the volume is nearly full
/// regardless of what settings say" pass across every camera on this node. Deletion isn't
/// time-critical the way starting/stopping ffmpeg is, so this polls independently of NodeWorker's
/// reconcile loop on its own (slower) cadence rather than competing with recording for attention.
///
/// Purely filesystem-driven: it never touches the database directly (NidusVMS.Node has no DB
/// connection — see the control-plane design in the architecture plan), it deletes files under the
/// storage root and reports the paths it removed back to the web via
/// <c>POST /api/nodes/segments/delete</c>, which is the only side that deletes the matching
/// <c>Segment</c> rows.
/// </summary>
public class StorageManager(NodeApiClient api, string fallbackStorageRoot, ILogger<StorageManager> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

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

        if (deletedPaths.Count > 0)
        {
            logger.LogInformation("Evicted {Count} segment(s) for storage limits.", deletedPaths.Count);
            await api.DeleteSegmentsAsync(deletedPaths, ct);
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
