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

    // Pass 2c: same slow cadence and independent gate as _lastReconciledAtUtc above, but its own
    // timestamp and its own try/catch in SweepAsync, so a segment-reconcile failure never blocks
    // snapshot cleanup or vice versa.
    private DateTime? _lastSnapshotsReconciledAtUtc;

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
            catch (Exception ex) when (!ct.IsCancellationRequested)
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

            // M7 pass 2: a hover-thumbnail cache has no reason to outlive the segment it was
            // extracted from — every eviction below deletes a segment's matching thumbnail(s)
            // alongside it rather than relying on a separate age sweep. Object detection plan
            // decision 10: cached snapshot-image crops get the identical guarantee, from their own
            // sibling directory.
            var thumbsDir = Path.Combine(storageRoot, $"cam-{camera.CameraId}", "thumbs");
            var snapshotsDir = Path.Combine(storageRoot, $"cam-{camera.CameraId}", "snapshots");

            var files = EnumerateEvictable(cameraDir, now);

            // Retention sweep: unconditional age cutoff. 0 or negative RetentionDays is an explicit
            // "keep forever" choice, not "unset" (unset falls through to NodeService's compiled-in
            // 30-day default before this DTO is ever built).
            foreach (var f in SelectRetentionEvictions(files, now, camera.RetentionDays))
            {
                if (TryDelete(f.FullName))
                {
                    deletedPaths.Add(f.FullName);
                    files.Remove(f);
                    DeleteMatchingThumbnails(cameraDir, thumbsDir, f.FullName);
                    DeleteMatchingSnapshotImages(cameraDir, snapshotsDir, f.FullName);
                }
            }

            // Per-camera quota: oldest-first until back under the cap.
            foreach (var f in SelectQuotaEvictions(files, camera.QuotaBytes))
            {
                if (TryDelete(f.FullName))
                {
                    deletedPaths.Add(f.FullName);
                    files.Remove(f);
                    DeleteMatchingThumbnails(cameraDir, thumbsDir, f.FullName);
                    DeleteMatchingSnapshotImages(cameraDir, snapshotsDir, f.FullName);
                }
            }

            // Pass G: the eager-crop staging files (snapshots/hires/{ticks}.jpg). Copied into the
            // span-keyed cache on first view but deliberately left in place (a second span sharing
            // that instant still needs to promote it), so nothing else ages them out. A staged file
            // has done its job within days of being written — after that a late first-view just
            // falls back to the segment-seek crop.
            foreach (var staged in SelectExpiredStagedCrops(snapshotsDir, now, camera.RetentionDays))
                if (TryDelete(staged)) deletedPaths.Add(staged);

            PruneEmptyDirectories(cameraDir);
            if (Directory.Exists(thumbsDir)) PruneEmptyDirectories(thumbsDir);
            if (Directory.Exists(snapshotsDir)) PruneEmptyDirectories(snapshotsDir);
        }

        SweepOrphanedCameraFolders(config, storageRoot, now, deletedPaths);

        await ApplyWatermarkAsync(config, storageRoot, now, deletedPaths, ct);

        // Export output files (and any stray concat list file ExportRunner didn't get to clean up
        // after a crash) — a sibling of the cam-{id}/ folders above, deliberately never walked by
        // the per-camera loop, so it needs its own pass. Age-only, no quota/watermark interaction:
        // exports are a small, occasional side product, not part of the retention/quota budget any
        // camera's continuous recording competes for. Not reported to /segments/delete — these were
        // never Segment rows, so there's nothing for the web tier to reconcile.
        SweepExportsDirectory(storageRoot, now);
        SweepLogsDirectory(now);

        if (_lastReconciledAtUtc is null || now - _lastReconciledAtUtc >= ReconcileInterval)
        {
            // Only stamped on success — a failed fetch (network blip, web tier restarting) should
            // retry on the next 5-minute sweep, not wait a full extra hour for the next scheduled one.
            if (await ReconcileAsync(config, storageRoot, deletedPaths, ct)) _lastReconciledAtUtc = now;
        }

        if (_lastSnapshotsReconciledAtUtc is null || now - _lastSnapshotsReconciledAtUtc >= ReconcileInterval)
        {
            try
            {
                if (await ReconcileSnapshotsAsync(storageRoot, ct)) _lastSnapshotsReconciledAtUtc = now;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Snapshot reconciliation sweep failed — will retry next cycle.");
            }
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
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // The files are already gone from disk regardless of whether this succeeds — only
                // the *report* is being retried, not the deletion itself.
                _pendingDeletionReports.AddRange(deletedPaths);
                logger.LogWarning(ex, "Failed to report {Count} deleted segment(s) to the server — will retry next sweep.", deletedPaths.Count);
            }
        }
    }

    // Confirmed live: once a camera is reassigned to a different node (or deleted entirely), its
    // leftover cam-{id}/ folder here has no NodeConfigCameraDto to carry a RetentionDays/QuotaBytes
    // value anymore — before this fix, the per-camera loop above (which only ever walks
    // config.Cameras) simply never looked at it again, so the footage sat on disk forever, never
    // evicted, and the admin "stale segment" warning (CameraService.GetStaleSegmentNodeIdsAsync,
    // driven by the Segments rows this sweep is what would otherwise delete) could never clear on
    // its own. Age-only, no quota — there's no camera-specific budget left to enforce. Retention
    // comes from config.OrphanedCameras (the server resolves it the same global -> per-node ->
    // per-camera way an assigned camera's RetentionDays is, just scoped to this node specifically —
    // see NodeService.GetConfigAsync) rather than a generic guess, so leftover footage still ages
    // out on the schedule it actually would have, not sooner or later. This flat fallback only
    // fires if the server genuinely has no answer for a given camera (e.g. it doesn't even appear
    // in OrphanedCameras — shouldn't normally happen for a folder that exists at all, but a fallback
    // beats leaving it unmanaged the way this whole fix exists to avoid).
    private static readonly TimeSpan OrphanedCameraFallbackRetention = TimeSpan.FromDays(30);

    private void SweepOrphanedCameraFolders(NodeConfigResponse config, string storageRoot, DateTime now, List<string> deletedPaths)
    {
        var retentionByCamera = config.OrphanedCameras.ToDictionary(o => o.CameraId, o => o.RetentionDays);

        foreach (var (cameraId, mainDir) in FindOrphanedCameraMainDirs(storageRoot, config.Cameras.Select(c => c.CameraId)))
        {
            var retentionDays = retentionByCamera.TryGetValue(cameraId, out var resolved) && resolved is { } d
                ? d
                : (int)OrphanedCameraFallbackRetention.TotalDays;

            var thumbsDir = Path.Combine(Path.GetDirectoryName(mainDir)!, "thumbs");
            var snapshotsDir = Path.Combine(Path.GetDirectoryName(mainDir)!, "snapshots");
            var files = EnumerateEvictable(mainDir, now);

            foreach (var f in SelectRetentionEvictions(files, now, retentionDays))
            {
                if (TryDelete(f.FullName))
                {
                    deletedPaths.Add(f.FullName);
                    DeleteMatchingThumbnails(mainDir, thumbsDir, f.FullName);
                    DeleteMatchingSnapshotImages(mainDir, snapshotsDir, f.FullName);
                }
            }

            PruneEmptyDirectories(mainDir);
            if (Directory.Exists(thumbsDir)) PruneEmptyDirectories(thumbsDir);
            if (Directory.Exists(snapshotsDir)) PruneEmptyDirectories(snapshotsDir);
        }
    }

    /// <summary>Pure selection extracted so it's testable against a real temp directory the same way
    /// EnumerateEvictable is — every "cam-{guid}/main" directory under storageRoot whose {guid}
    /// doesn't match any id in assignedCameraIds, paired with the parsed CameraId so the caller can
    /// look up its retention.</summary>
    internal static List<(Guid CameraId, string MainDir)> FindOrphanedCameraMainDirs(string storageRoot, IEnumerable<Guid> assignedCameraIds)
    {
        if (!Directory.Exists(storageRoot)) return [];
        var assigned = assignedCameraIds.ToHashSet();

        var result = new List<(Guid, string)>();
        foreach (var cameraDir in Directory.EnumerateDirectories(storageRoot, "cam-*", SearchOption.TopDirectoryOnly))
        {
            var dirName = Path.GetFileName(cameraDir);
            var idPart = dirName.Length > 4 ? dirName[4..] : "";
            if (!Guid.TryParse(idPart, out var cameraId) || assigned.Contains(cameraId)) continue;

            var mainDir = Path.Combine(cameraDir, "main");
            if (Directory.Exists(mainDir)) result.Add((cameraId, mainDir));
        }
        return result;
    }

    private async Task ApplyWatermarkAsync(NodeConfigResponse config, string storageRoot,
        DateTime now, List<string> deletedPaths, CancellationToken ct)
    {
        var usage = DiskSpace.TryGetUsage(storageRoot);
        if (usage is not { } u || u.TotalBytes <= 0) return;
        if (100.0 * (u.TotalBytes - u.FreeBytes) / u.TotalBytes <= config.WatermarkPercent) return;

        // Global oldest-first across every camera on this node — retention/quota already ran, so
        // whatever's left here is "in policy" but the volume is full anyway; age is the only fair
        // tiebreaker across cameras with different quotas/retention. Carries CameraId/MainDir
        // alongside each FileInfo (not just the file) so a deletion here can also clean up that
        // segment's cached thumbnails, same as the retention/quota loops above.
        var candidates = config.Cameras
            .Select(c => (c.CameraId, MainDir: Path.Combine(storageRoot, $"cam-{c.CameraId}", "main")))
            .Where(x => Directory.Exists(x.MainDir))
            .SelectMany(x => EnumerateEvictable(x.MainDir, now).Select(f => (x.CameraId, x.MainDir, File: f)))
            .OrderBy(x => x.File.LastWriteTimeUtc);

        foreach (var c in candidates)
        {
            var current = DiskSpace.TryGetUsage(storageRoot);
            if (current is null) break;
            if (100.0 * (current.Value.TotalBytes - current.Value.FreeBytes) / current.Value.TotalBytes <= config.WatermarkPercent) break;

            if (TryDelete(c.File.FullName))
            {
                deletedPaths.Add(c.File.FullName);
                var thumbsDir = Path.Combine(storageRoot, $"cam-{c.CameraId}", "thumbs");
                var snapshotsDir = Path.Combine(storageRoot, $"cam-{c.CameraId}", "snapshots");
                DeleteMatchingThumbnails(c.MainDir, thumbsDir, c.File.FullName);
                DeleteMatchingSnapshotImages(c.MainDir, snapshotsDir, c.File.FullName);
            }
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
    /// <summary>
    /// Recovers footage that exists on disk but has no <c>Segment</c> row — the reverse of the
    /// deletion reconciliation below, and the repair path for the reporting outage fixed in 0.150.0
    /// (a node whose report loop died kept recording perfectly while the server was never told, so
    /// hours of real footage sat on disk invisible to Playback). Also covers any future case where a
    /// report was lost for good: the file on disk is the source of truth, so anything under a camera's
    /// own recording directory that the web tier doesn't know about gets offered back to it.
    ///
    /// Safe to run repeatedly: <c>Segment.FilePath</c> is uniquely indexed and
    /// <c>NodeService.RecordSegmentsAsync</c> already detaches duplicate-key rows rather than failing
    /// the batch, so re-reporting something already known is a no-op rather than an error.
    ///
    /// Timestamps come from file metadata, never from the filename: ffmpeg's <c>-strftime</c> pattern
    /// writes names in the node's *local* zone despite their trailing "Z" (see RecordingSession's own
    /// comment on why that's deliberate), so parsing them as UTC would silently offset every recovered
    /// segment by the node's UTC offset. CreationTimeUtc/LastWriteTimeUtc are unambiguous and are what
    /// the normal reporting path already derives its own start/end from.
    /// </summary>
    private async Task ImportOrphanedSegmentsAsync(NodeConfigResponse config, string storageRoot,
        List<string> knownPaths, CancellationToken ct)
    {
        var known = new HashSet<string>(knownPaths, StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        var toImport = new List<SegmentReportItem>();

        foreach (var camera in config.Cameras)
        {
            var cameraDir = Path.Combine(storageRoot, $"cam-{camera.CameraId}", "main");
            if (!Directory.Exists(cameraDir)) continue;

            List<FileInfo> files;
            // Same MinAge floor the eviction loops use — never touch the segment ffmpeg may still be
            // writing, which has no meaningful end time yet and would import as a truncated row that
            // the normal reporting path is about to report correctly anyway.
            try { files = EnumerateEvictable(cameraDir, now); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not scan {CameraDir} for unreported footage — skipping this camera in this sweep.", cameraDir);
                continue;
            }

            toImport.AddRange(SelectImportableSegments(camera.CameraId, files, known));
        }

        if (toImport.Count == 0) return;

        try
        {
            await api.ReportSegmentsAsync(toImport, ct);
            logger.LogInformation(
                "Imported {Count} segment(s) found on disk with no database row — recovered footage is now visible in Playback.",
                toImport.Count);
        }
        catch (Exception ex) when (NodeWorker.IsRetryable(ex, ct))
        {
            // Not re-queued: the files stay on disk, so the very next reconcile rediscovers them
            // exactly the same way. Nothing is lost by simply trying again later.
            logger.LogWarning(ex, "Failed to import {Count} recovered segment(s) — will retry next reconcile.", toImport.Count);
        }
    }

    private async Task<bool> ReconcileAsync(NodeConfigResponse config, string storageRoot, List<string> deletedPaths, CancellationToken ct)
    {
        List<string> knownPaths;
        try
        {
            knownPaths = await api.GetSegmentFilePathsAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Reconciliation sweep could not fetch this node's known segment paths — will retry next sweep.");
            return false;
        }

        // Guard 1: prove storage is genuinely reachable before believing any File.Exists miss. See
        // StorageHealth's own doc comment — an unreachable SMB share reports every path as simply
        // "not there", which would otherwise delete every row this node owns for footage still on disk.
        if (!StorageHealth.CanReachStorage(storageRoot))
        {
            logger.LogWarning(
                "Reconciliation sweep skipped — storage root {StorageRoot} did not pass a read/write probe, so a missing file can't be told apart from an unreachable share. Will retry next sweep.",
                storageRoot);
            return false;
        }

        // The other direction: footage on disk the web tier has no row for. Runs before the deletion
        // inference below and independently of it — an import can never lose data, so it doesn't need
        // the same paranoia the deletion path does (the reachability probe above is enough).
        await ImportOrphanedSegmentsAsync(config, storageRoot, knownPaths, ct);

        var missing = SelectMissingPaths(knownPaths);
        if (missing.Count == 0) return true;

        // Guard 2: re-check every candidate after a moment. A blip that starts *after* the probe above
        // passed still lands here, and a genuinely deleted file stays gone on a second look — so this
        // costs one extra stat per candidate (a small list in normal operation) and removes the entire
        // class of "transient unavailability read as deletion".
        try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
        catch (OperationCanceledException) { return false; }

        if (!StorageHealth.CanReachStorage(storageRoot))
        {
            logger.LogWarning("Reconciliation sweep abandoned — storage stopped responding while re-verifying {Count} candidate(s). Will retry next sweep.", missing.Count);
            return false;
        }

        var confirmed = SelectMissingPaths(missing);
        if (confirmed.Count < missing.Count)
        {
            logger.LogWarning(
                "Reconciliation sweep: {Recovered} of {Candidates} candidate(s) reappeared on re-check — storage was briefly unavailable, not genuinely missing. Only confirmed deletions are being reported.",
                missing.Count - confirmed.Count, missing.Count);
        }
        if (confirmed.Count == 0) return true;

        // Guard 3: a mass disappearance is a fault, not a fleet of real deletions this sweep somehow
        // didn't perform itself. Refusing here only delays genuine orphan cleanup by one sweep; acting
        // wrongly permanently drops rows for footage that still exists.
        if (StorageHealth.IsImplausibleMissingCount(confirmed.Count, knownPaths.Count))
        {
            logger.LogError(
                "Reconciliation sweep refusing to report {Count} of {Total} segment(s) as deleted — that proportion indicates a storage fault, not real deletions. Nothing has been reported; investigate the storage backend, then this will resolve itself on a later sweep once storage is healthy.",
                confirmed.Count, knownPaths.Count);
            return false;
        }

        logger.LogInformation(
            "Reconciliation sweep found {Count} segment row(s) (of {Total} checked) pointing at files no longer on disk — reporting for cleanup.",
            confirmed.Count, knownPaths.Count);
        deletedPaths.AddRange(confirmed);

        return true;
    }

    /// <summary>Pure selection extracted from ReconcileAsync so it's testable against a real temp
    /// directory the same way EnumerateEvictable is, without a network round trip.</summary>
    internal static List<string> SelectMissingPaths(IEnumerable<string> knownPaths)
        => knownPaths.Where(p => !File.Exists(p)).ToList();

    // Pass 2c: {segment-stem}_span{spanId}.jpg — the same naming convention Program.cs's own
    // snapshot-cache writer uses.
    private static readonly System.Text.RegularExpressions.Regex SnapshotSpanIdPattern =
        new(@"_span(\d+)\.jpg$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Pass 2c: self-heals cached snapshot-crop files whose owning MotionSpan row was
    /// deleted independently of the segment it was cropped from (MotionSpanRetentionService's own
    /// 6-hour sweep), rather than as a side effect of that segment's own eviction — which
    /// DeleteMatchingSnapshotImages above already handles. Mirrors ReconcileAsync's guard shape:
    /// prove storage is reachable, re-check after a delay before trusting the result, then refuse a
    /// mass deletion that looks more like a fault than real cleanup. Unlike ReconcileAsync, what's
    /// "missing" here comes from an API response (the still-valid span ids), not a File.Exists probe
    /// that can itself flap — so the re-check re-verifies storage health, not the orphan set.</summary>
    private async Task<bool> ReconcileSnapshotsAsync(string storageRoot, CancellationToken ct)
    {
        HashSet<long> knownSpanIds;
        try
        {
            knownSpanIds = (await api.GetMotionSpanIdsAsync(ct)).ToHashSet();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Snapshot reconciliation sweep could not fetch this node's known motion-span ids — will retry next sweep.");
            return false;
        }

        if (!StorageHealth.CanReachStorage(storageRoot))
        {
            logger.LogWarning(
                "Snapshot reconciliation sweep skipped — storage root {StorageRoot} did not pass a read/write probe. Will retry next sweep.",
                storageRoot);
            return false;
        }

        var allFiles = EnumerateSnapshotFiles(storageRoot);
        var orphaned = SelectOrphanedSnapshotFiles(allFiles, knownSpanIds);
        if (orphaned.Count == 0) return true;

        try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
        catch (OperationCanceledException) { return false; }

        if (!StorageHealth.CanReachStorage(storageRoot))
        {
            logger.LogWarning(
                "Snapshot reconciliation sweep abandoned — storage stopped responding while re-verifying {Count} candidate(s). Will retry next sweep.",
                orphaned.Count);
            return false;
        }

        if (StorageHealth.IsImplausibleMissingCount(orphaned.Count, allFiles.Count))
        {
            logger.LogError(
                "Snapshot reconciliation sweep refusing to delete {Count} of {Total} cached snapshot file(s) — that proportion indicates a fault (e.g. an empty span-id response), not real orphans. Nothing has been deleted; this will resolve itself on a later sweep.",
                orphaned.Count, allFiles.Count);
            return false;
        }

        logger.LogInformation(
            "Snapshot reconciliation sweep found {Count} cached snapshot file(s) (of {Total} checked) with no matching MotionSpan row — deleting.",
            orphaned.Count, allFiles.Count);
        foreach (var file in orphaned) TryDelete(file);

        return true;
    }

    /// <summary>Pass G: staged eager-crop files (<c>snapshots/hires/{ticks}.jpg</c>) older than a
    /// few days — bounded at the camera's own retention window, capped at 7 days. Tick-named, so a
    /// filename that doesn't parse as a positive Int64 is left alone. Pure/testable like the other
    /// selectors here.</summary>
    internal static List<string> SelectExpiredStagedCrops(string snapshotsDir, DateTime now, int? retentionDays)
    {
        var hiresDir = Path.Combine(snapshotsDir, "hires");
        if (!Directory.Exists(hiresDir)) return [];

        var maxAgeDays = retentionDays is > 0 ? Math.Min(retentionDays.Value, 7) : 7;
        var cutoff = now.AddDays(-maxAgeDays);

        var result = new List<string>();
        foreach (var file in Directory.EnumerateFiles(hiresDir, "*.jpg", SearchOption.TopDirectoryOnly))
        {
            if (!long.TryParse(Path.GetFileNameWithoutExtension(file), out var ticks) || ticks <= 0 || ticks > DateTime.MaxValue.Ticks)
                continue;
            if (new DateTime(ticks, DateTimeKind.Utc) < cutoff) result.Add(file);
        }
        return result;
    }

    /// <summary>Pure so it's unit-testable against a real temp directory without a network round
    /// trip — every cached snapshot-crop file under every camera's own snapshots/ folder.</summary>
    internal static List<string> EnumerateSnapshotFiles(string storageRoot)
        => Directory.Exists(storageRoot)
            ? Directory.EnumerateDirectories(storageRoot, "cam-*", SearchOption.TopDirectoryOnly)
                .Select(camDir => Path.Combine(camDir, "snapshots"))
                .Where(Directory.Exists)
                .SelectMany(snapshotsDir => Directory.EnumerateFiles(snapshotsDir, "*_span*.jpg", SearchOption.AllDirectories))
                .ToList()
            : [];

    /// <summary>Which of these cached snapshot files has no MotionSpan row behind it any more —
    /// parses the trailing "_span{id}.jpg" each file is named with (Program.cs's own writer) and
    /// keeps only the ones whose id isn't in <paramref name="knownSpanIds"/>. A filename that
    /// doesn't parse is left alone, never deleted — it isn't this convention's file to judge.</summary>
    internal static List<string> SelectOrphanedSnapshotFiles(IEnumerable<string> files, HashSet<long> knownSpanIds)
    {
        var result = new List<string>();
        foreach (var file in files)
        {
            var match = SnapshotSpanIdPattern.Match(file);
            if (!match.Success) continue;
            if (!long.TryParse(match.Groups[1].Value, out var spanId)) continue;
            if (!knownSpanIds.Contains(spanId)) result.Add(file);
        }
        return result;
    }

    /// <summary>Which of this camera's on-disk files the web tier has no row for — the pure half of
    /// <see cref="ImportOrphanedSegmentsAsync"/>, extracted for the same reason every other decision in
    /// this class is. Paths are compared case-insensitively: these are Windows/UNC paths, where the
    /// same file can legitimately be spelled with different casing than the row that recorded it, and
    /// treating those as different would re-import a segment that is already known on every sweep.</summary>
    internal static List<SegmentReportItem> SelectImportableSegments(Guid cameraId, IEnumerable<FileInfo> files,
        HashSet<string> knownPaths)
    {
        var result = new List<SegmentReportItem>();
        foreach (var file in files)
        {
            if (knownPaths.Contains(file.FullName)) continue;
            // Same 0-byte guard PollForCompletedSegments applies: ffmpeg creates the file before it can
            // fail the header write, so an empty file is a failed-connection artifact, never a real
            // recording.
            if (file.Length == 0) continue;

            // LastWriteTimeUtc (when writing finished) rather than a next-file boundary: for an
            // already-closed file this is its true end, and unlike the live path there's no in-flight
            // observation to prefer over it. Guarded because a file whose metadata is nonsensical (a
            // clock change mid-write) would otherwise import a zero or negative duration.
            var start = file.CreationTimeUtc;
            var end = file.LastWriteTimeUtc;
            if (end <= start) continue;

            result.Add(new SegmentReportItem(cameraId, "Main", start, end,
                file.FullName, file.Length, Codec: null, Width: null, Height: null, HasAudio: false));
        }
        return result;
    }

    /// <summary>Every cached thumbnail file belonging to mainFilePath (M7 pass 2) — derived purely
    /// from the segment's own relative path/filename under mainDir (never client-supplied), globbing
    /// every bucketed-offset variant ("_o00.jpg", "_o05.jpg", ...) a hover request may have generated
    /// for it. Pure/testable against a real temp directory, same pattern as EnumerateEvictable.</summary>
    internal static List<string> FindMatchingThumbnails(string mainDir, string thumbsDir, string mainFilePath)
    {
        if (!Directory.Exists(thumbsDir)) return [];
        var relative = Path.GetRelativePath(mainDir, mainFilePath);
        var relativeDir = Path.GetDirectoryName(relative) ?? "";
        var stem = Path.GetFileNameWithoutExtension(relative);
        var thumbDirForSegment = Path.Combine(thumbsDir, relativeDir);
        if (!Directory.Exists(thumbDirForSegment)) return [];
        return Directory.EnumerateFiles(thumbDirForSegment, stem + "_o*.jpg", SearchOption.TopDirectoryOnly).ToList();
    }

    private void DeleteMatchingThumbnails(string mainDir, string thumbsDir, string mainFilePath)
    {
        foreach (var thumb in FindMatchingThumbnails(mainDir, thumbsDir, mainFilePath)) TryDelete(thumb);
    }

    /// <summary>Object detection plan decision 10: every cached snapshot-image file belonging to
    /// mainFilePath — same derivation as FindMatchingThumbnails, but globbing "_span*.jpg" (one
    /// MotionSpan's own id) instead of "_o*.jpg" (a bucketed time offset), since a segment can carry
    /// more than one detected object, each with its own owning span and its own cached crop.</summary>
    internal static List<string> FindMatchingSnapshotImages(string mainDir, string snapshotsDir, string mainFilePath)
    {
        if (!Directory.Exists(snapshotsDir)) return [];
        var relative = Path.GetRelativePath(mainDir, mainFilePath);
        var relativeDir = Path.GetDirectoryName(relative) ?? "";
        var stem = Path.GetFileNameWithoutExtension(relative);
        var snapshotDirForSegment = Path.Combine(snapshotsDir, relativeDir);
        if (!Directory.Exists(snapshotDirForSegment)) return [];
        return Directory.EnumerateFiles(snapshotDirForSegment, stem + "_span*.jpg", SearchOption.TopDirectoryOnly).ToList();
    }

    private void DeleteMatchingSnapshotImages(string mainDir, string snapshotsDir, string mainFilePath)
    {
        foreach (var snapshot in FindMatchingSnapshotImages(mainDir, snapshotsDir, mainFilePath)) TryDelete(snapshot);
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

    // M11: FileLoggerProvider's own daily-rolling files under %ProgramData%\LarisVMS\logs — a fixed,
    // non-settings-driven window (unlike Backup's admin-configurable retention count) since this is a
    // small text-file cleanup with none of a multi-GB backup's disk-space stakes, and a node must keep
    // sweeping this on its own local schedule even when it can't reach the web tier to read a setting.
    private static readonly TimeSpan LogRetention = TimeSpan.FromDays(14);

    // "vision-*.log" added alongside "node-*.log" once LarisVMS.Vision.Service gained its own
    // FileLoggerProvider (detection/hardware-acceleration overhaul pass 3b) — same shared logs
    // directory, same retention window, so a sibling process's log doesn't accumulate forever just
    // because this sweep only ever knew about Node's own file prefix.
    private static readonly string[] LogFilePatterns = ["node-*.log", "vision-*.log"];

    private void SweepLogsDirectory(DateTime now)
    {
        var logsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LarisVMS", "logs");
        if (!Directory.Exists(logsDir)) return;

        foreach (var pattern in LogFilePatterns)
        {
            foreach (var path in Directory.EnumerateFiles(logsDir, pattern, SearchOption.TopDirectoryOnly))
            {
                FileInfo info;
                try { info = new FileInfo(path); }
                catch (IOException) { continue; }

                if (now - info.LastWriteTimeUtc > LogRetention) TryDelete(path);
            }
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
