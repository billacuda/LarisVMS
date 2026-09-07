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

    // Archive storage: catches segment rows still marked StorageTier=Primary whose file is
    // physically under the archive root — the case where an admin repoints a node's storage root to
    // a new volume and demotes the old one to the archive root, so footage the node never "moved" is
    // suddenly on the archive volume. Same slow (hourly) cadence and independent gate as the others.
    private DateTime? _lastArchiveTierReconciledAtUtc;

    // Files this deleted from disk but hasn't yet successfully told the web tier about — carried
    // over to the next sweep's report attempt on failure. Without this, a deletion report that
    // fails (a network blip, the web tier restarting mid-sweep) permanently orphans the Segment
    // row: the file is already gone from disk by the time the report is attempted, so a later
    // sweep's EnumerateEvictable can never rediscover it to try again. Every other node→web report
    // in this codebase (segments, stream info, motion spans) already re-queues on failure; this one
    // didn't. Plain List, not a ConcurrentQueue: SweepAsync only ever runs from this single
    // BackgroundService's own sequential loop, never concurrently with itself.
    private readonly List<string> _pendingDeletionReports = [];

    // Archive storage: a segment file this MOVED to the archive volume but hasn't yet successfully
    // told the web tier about (POST /api/nodes/segments/relocate). Same re-queue-on-failure reason as
    // _pendingDeletionReports above, but the stakes are higher: the source file is deliberately kept
    // on the primary volume until the relocate report lands, so a failed report just retries with
    // both copies present. Carries the primary-side cache dirs so the source's thumbnails/snapshots
    // moved to the archive alongside it once the report succeeds — a segment's cached hover
    // thumbnails and AI-detection snapshot crops follow the video so archived footage keeps its fast
    // previews and they expire together with it, rather than being deleted and regenerated slowly
    // from a cold-storage seek. ReconcileAsync excludes any OldFilePath still in this list from its
    // "missing file -> delete the row" inference.
    private readonly record struct PendingRelocation(
        LarisVMS.Core.Dtos.SegmentRelocateItem Item,
        string PrimaryMainDir, string PrimaryThumbsDir, string PrimarySnapshotsDir,
        string ArchiveThumbsDir, string ArchiveSnapshotsDir);
    private readonly List<PendingRelocation> _pendingRelocationReports = [];

    // Leave this much headroom on the archive volume when deciding whether a file can be moved there
    // — a partial copy that fills the archive disk helps nobody.
    private const long ArchiveFreeSpaceMargin = 512L * 1024 * 1024;

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
        var relocations = new List<PendingRelocation>();

        // Archive storage (phase 2): the archive volume is usable when it's configured, sits outside
        // the storage root (or its files would be re-imported as new primary segments), and passes a
        // read/write probe. Checked once per sweep — a per-file failure inside TryArchiveFile is
        // handled there.
        var archiveRoot = string.IsNullOrWhiteSpace(config.ArchiveRootPath) ? null : config.ArchiveRootPath;
        var archiveUsable = false;
        if (archiveRoot is not null)
        {
            if (IsUnderStorageRoot(archiveRoot, storageRoot))
            {
                logger.LogWarning("Archive root {ArchiveRoot} is inside the storage root {StorageRoot} — archiving is disabled this sweep. Point it at a separate volume.", archiveRoot, storageRoot);
            }
            else
            {
                try { Directory.CreateDirectory(archiveRoot); } catch { /* CanReachStorage reports the failure below */ }
                archiveUsable = StorageHealth.CanReachStorage(archiveRoot);
                if (!archiveUsable)
                    logger.LogWarning("Archive root {ArchiveRoot} did not pass a read/write probe — footage that would be archived is being kept on primary this sweep.", archiveRoot);
            }
        }

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
            var archiveCameraMainDir = archiveRoot is null ? "" : Path.Combine(archiveRoot, $"cam-{camera.CameraId}", "main");
            var archiveThisCamera = camera.ArchiveEnabled && archiveUsable;

            var files = EnumerateEvictable(cameraDir, now);

            // Retention sweep: unconditional age cutoff. 0 or negative RetentionDays is an explicit
            // "keep forever" choice, not "unset" (unset falls through to NodeService's compiled-in
            // 30-day default before this DTO is ever built). When archiving is on for this camera, a
            // retention eviction MOVES the file to the archive volume instead of deleting it — see
            // ArchiveOrDelete.
            foreach (var f in SelectRetentionEvictions(files, now, camera.RetentionDays))
                if (ArchiveOrDelete(f, cameraDir, thumbsDir, snapshotsDir, archiveCameraMainDir, archiveThisCamera, archiveRoot, deletedPaths, relocations))
                    files.Remove(f);

            // Per-camera quota: oldest-first until back under the cap. Same archive-or-delete branch.
            foreach (var f in SelectQuotaEvictions(files, camera.QuotaBytes))
                if (ArchiveOrDelete(f, cameraDir, thumbsDir, snapshotsDir, archiveCameraMainDir, archiveThisCamera, archiveRoot, deletedPaths, relocations))
                    files.Remove(f);

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

        // Archive-expiry: footage on the archive volume past that camera's ArchiveRetentionDays (from
        // the file's own mtime, which TryArchiveFile anchored to the recording time). Genuinely gone
        // now — reported through the normal delete path so the row is removed.
        if (archiveUsable && archiveRoot is not null)
            ExpireArchivedFootage(config, archiveRoot, now, deletedPaths);

        SweepOrphanedCameraFolders(config, storageRoot, archiveRoot, archiveUsable, now, deletedPaths, relocations);

        ApplyWatermark(config, storageRoot, archiveRoot, archiveUsable, now, deletedPaths, relocations);

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
            // Old paths of segments this or a prior sweep archived but hasn't finished reporting —
            // their source is deliberately still on primary, so File.Exists says "present", but the
            // row is (or is about to be) at the archive path. Excluded from the missing-file
            // inference so a mid-flight relocation is never read as a deletion.
            var relocatingOldPaths = relocations.Select(r => r.Item.OldFilePath)
                .Concat(_pendingRelocationReports.Select(r => r.Item.OldFilePath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Only stamped on success — a failed fetch (network blip, web tier restarting) should
            // retry on the next 5-minute sweep, not wait a full extra hour for the next scheduled one.
            if (await ReconcileAsync(config, storageRoot, archiveRoot, relocatingOldPaths, deletedPaths, ct)) _lastReconciledAtUtc = now;
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

        if (archiveUsable && archiveRoot is not null
            && (_lastArchiveTierReconciledAtUtc is null || now - _lastArchiveTierReconciledAtUtc >= ReconcileInterval))
        {
            try
            {
                if (await ReconcileArchiveTierAsync(config, archiveRoot, relocations, ct)) _lastArchiveTierReconciledAtUtc = now;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Archive-tier reconciliation sweep failed — will retry next cycle.");
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

        // Relocations (archive moves): the source is still on the primary volume — only removed once
        // the web has updated the row to the archive path. A failed report keeps both copies and
        // retries next sweep (TryArchiveFile treats a pre-existing matching target as done).
        relocations.AddRange(_pendingRelocationReports);
        _pendingRelocationReports.Clear();

        if (relocations.Count > 0)
        {
            try
            {
                await api.RelocateSegmentsAsync(relocations.Select(r => r.Item).ToList(), ct);
                logger.LogInformation("Archived {Count} segment(s) to the archive volume.", relocations.Count);
                foreach (var r in relocations)
                {
                    // A tier-flip reconcile (ReconcileArchiveTierAsync) reports OldFilePath ==
                    // NewFilePath — the file is already on the archive volume and there is nothing to
                    // move. Only a real move has a distinct primary source + caches to relocate.
                    if (string.Equals(r.Item.OldFilePath, r.Item.NewFilePath, StringComparison.OrdinalIgnoreCase)) continue;
                    TryDelete(r.Item.OldFilePath); // the primary .mp4 — already copied to the archive by TryArchiveFile
                    // Move the segment's cached hover thumbnails and AI-detection snapshot crops onto
                    // the archive volume too, so archived footage keeps its fast previews and they
                    // expire together with it (a delete-and-regenerate would mean a slow cold-storage
                    // seek per preview, and the crops would otherwise be swept on the primary
                    // retention schedule, not the archive one).
                    MoveCachedMediaToArchive(FindMatchingThumbnails(r.PrimaryMainDir, r.PrimaryThumbsDir, r.Item.OldFilePath),
                        r.PrimaryThumbsDir, r.ArchiveThumbsDir);
                    MoveCachedMediaToArchive(FindMatchingSnapshotImages(r.PrimaryMainDir, r.PrimarySnapshotsDir, r.Item.OldFilePath),
                        r.PrimarySnapshotsDir, r.ArchiveSnapshotsDir);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _pendingRelocationReports.AddRange(relocations);
                logger.LogWarning(ex, "Failed to report {Count} archived segment(s) to the server — sources kept on primary, will retry next sweep.", relocations.Count);
            }
        }
    }

    /// <summary>Retention/quota eviction: archive the file to the archive volume (and queue a
    /// relocation report) when archiving is on for this camera and the archive has room; otherwise
    /// delete it and its cached thumbnails/snapshots as before. Returns true if the file was handled
    /// (so the caller can drop it from its working set). Returns false — keeping the file for a later
    /// retry — when an archive move was wanted but failed (a locked file, a transient I/O error): the
    /// point of archiving is to NOT lose footage while there's somewhere to put it.</summary>
    private bool ArchiveOrDelete(FileInfo f, string primaryMainDir, string primaryThumbsDir, string primarySnapshotsDir,
        string archiveCameraMainDir, bool archiveThisCamera, string? archiveRoot,
        List<string> deletedPaths, List<PendingRelocation> relocations)
    {
        if (archiveThisCamera && archiveRoot is not null && ArchiveHasRoom(archiveRoot, f.Length))
        {
            var target = BuildArchiveTargetPath(primaryMainDir, archiveCameraMainDir, f.FullName);
            if (TryArchiveFile(f.FullName, target, logger))
            {
                var archiveCameraDir = Path.GetDirectoryName(archiveCameraMainDir)!; // {archiveRoot}/cam-{id}
                relocations.Add(new PendingRelocation(
                    new LarisVMS.Core.Dtos.SegmentRelocateItem(f.FullName, target, f.Length),
                    primaryMainDir, primaryThumbsDir, primarySnapshotsDir,
                    Path.Combine(archiveCameraDir, "thumbs"), Path.Combine(archiveCameraDir, "snapshots")));
                return true;
            }
            return false; // wanted to archive, couldn't — keep it, don't delete
        }

        if (TryDelete(f.FullName))
        {
            deletedPaths.Add(f.FullName);
            DeleteMatchingThumbnails(primaryMainDir, primaryThumbsDir, f.FullName);
            DeleteMatchingSnapshotImages(primaryMainDir, primarySnapshotsDir, f.FullName);
            return true;
        }
        return false;
    }

    private static bool ArchiveHasRoom(string archiveRoot, long fileLength)
    {
        var usage = DiskSpace.TryGetUsage(archiveRoot);
        return usage is { } u && u.FreeBytes > fileLength + ArchiveFreeSpaceMargin;
    }

    /// <summary>Moves each cached thumbnail/snapshot file from under <paramref name="fromRootDir"/> to
    /// the same relative location under <paramref name="toRootDir"/>. Best-effort: a file that can't
    /// be moved is deleted instead so it doesn't linger on the primary volume and get swept on the
    /// wrong schedule — the archive endpoint just regenerates that one preview on demand.</summary>
    private void MoveCachedMediaToArchive(IEnumerable<string> files, string fromRootDir, string toRootDir)
    {
        foreach (var src in files)
        {
            var dest = Path.Combine(toRootDir, Path.GetRelativePath(fromRootDir, src));
            try
            {
                var destDir = Path.GetDirectoryName(dest);
                if (destDir is not null) Directory.CreateDirectory(destDir);
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(src, dest); // cross-volume Move = copy + delete
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not move cached preview {Src} to the archive volume — deleting it; the archive will regenerate it on demand.", src);
                try { File.Delete(src); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>Deletes archive-volume footage past that camera's ArchiveRetentionDays — measured
    /// from the file's own mtime, which TryArchiveFile stamped to the recording time. Reuses the
    /// same pure age selector the primary retention sweep uses.</summary>
    private void ExpireArchivedFootage(NodeConfigResponse config, string archiveRoot, DateTime now, List<string> deletedPaths)
    {
        foreach (var camera in config.Cameras)
        {
            var archiveCameraDir = Path.Combine(archiveRoot, $"cam-{camera.CameraId}", "main");
            if (!Directory.Exists(archiveCameraDir)) continue;

            var archiveThumbsDir = Path.Combine(archiveRoot, $"cam-{camera.CameraId}", "thumbs");
            var archiveSnapshotsDir = Path.Combine(archiveRoot, $"cam-{camera.CameraId}", "snapshots");

            foreach (var f in SelectRetentionEvictions(EnumerateEvictable(archiveCameraDir, now), now, camera.ArchiveRetentionDays))
            {
                if (TryDelete(f.FullName))
                {
                    deletedPaths.Add(f.FullName);
                    DeleteMatchingThumbnails(archiveCameraDir, archiveThumbsDir, f.FullName);
                    DeleteMatchingSnapshotImages(archiveCameraDir, archiveSnapshotsDir, f.FullName);
                }
            }

            PruneEmptyDirectories(archiveCameraDir);
            if (Directory.Exists(archiveThumbsDir)) PruneEmptyDirectories(archiveThumbsDir);
            if (Directory.Exists(archiveSnapshotsDir)) PruneEmptyDirectories(archiveSnapshotsDir);
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

    private void SweepOrphanedCameraFolders(NodeConfigResponse config, string storageRoot, string? archiveRoot,
        bool archiveUsable, DateTime now, List<string> deletedPaths, List<PendingRelocation> relocations)
    {
        var orphanById = config.OrphanedCameras.ToDictionary(o => o.CameraId, o => o);
        var assignedIds = config.Cameras.Select(c => c.CameraId).ToList();

        // Primary-volume leftovers: same age cutoff, and same archive-or-delete branch — an orphaned
        // camera whose ArchiveEnabled is still set keeps its footage on the archive volume rather
        // than losing it.
        foreach (var (cameraId, mainDir) in FindOrphanedCameraMainDirs(storageRoot, assignedIds))
        {
            var orphan = orphanById.GetValueOrDefault(cameraId);
            var retentionDays = orphan?.RetentionDays ?? (int)OrphanedCameraFallbackRetention.TotalDays;
            var thumbsDir = Path.Combine(Path.GetDirectoryName(mainDir)!, "thumbs");
            var snapshotsDir = Path.Combine(Path.GetDirectoryName(mainDir)!, "snapshots");
            var archiveCameraMainDir = archiveRoot is null ? "" : Path.Combine(archiveRoot, $"cam-{cameraId}", "main");
            var archiveThisOrphan = (orphan?.ArchiveEnabled ?? false) && archiveUsable;

            foreach (var f in SelectRetentionEvictions(EnumerateEvictable(mainDir, now), now, retentionDays))
                ArchiveOrDelete(f, mainDir, thumbsDir, snapshotsDir, archiveCameraMainDir, archiveThisOrphan, archiveRoot, deletedPaths, relocations);

            PruneEmptyDirectories(mainDir);
            if (Directory.Exists(thumbsDir)) PruneEmptyDirectories(thumbsDir);
            if (Directory.Exists(snapshotsDir)) PruneEmptyDirectories(snapshotsDir);
        }

        // Archive-volume leftovers for the same cameras: age out at ArchiveRetentionDays.
        if (archiveUsable && archiveRoot is not null)
        {
            foreach (var (cameraId, mainDir) in FindOrphanedCameraMainDirs(archiveRoot, assignedIds))
            {
                var orphan = orphanById.GetValueOrDefault(cameraId);
                var archiveRetentionDays = orphan?.ArchiveRetentionDays ?? (int)OrphanedCameraFallbackRetention.TotalDays;
                var thumbsDir = Path.Combine(Path.GetDirectoryName(mainDir)!, "thumbs");
                var snapshotsDir = Path.Combine(Path.GetDirectoryName(mainDir)!, "snapshots");

                foreach (var f in SelectRetentionEvictions(EnumerateEvictable(mainDir, now), now, archiveRetentionDays))
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

    private void ApplyWatermark(NodeConfigResponse config, string storageRoot, string? archiveRoot,
        bool archiveUsable, DateTime now, List<string> deletedPaths, List<PendingRelocation> relocations)
    {
        var usage = DiskSpace.TryGetUsage(storageRoot);
        if (usage is not { } u || u.TotalBytes <= 0) return;
        if (100.0 * (u.TotalBytes - u.FreeBytes) / u.TotalBytes <= config.WatermarkPercent) return;

        var archiveEnabledByCamera = config.Cameras.ToDictionary(c => c.CameraId, c => c.ArchiveEnabled);

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

        // Bytes we've queued to free but not yet actually removed — an archived file's source isn't
        // deleted until the relocate report lands at the end of this sweep, and a deleted file's
        // space is reclaimed immediately. Tracking the projected free space lets the loop stop once
        // it's committed enough work to get under the watermark instead of archiving every last file.
        long projectedFreed = 0;
        var storagePercentTarget = config.WatermarkPercent;

        foreach (var c in candidates)
        {
            var current = DiskSpace.TryGetUsage(storageRoot);
            if (current is null) break;
            var projectedFree = current.Value.FreeBytes + projectedFreed;
            if (100.0 * (current.Value.TotalBytes - projectedFree) / current.Value.TotalBytes <= storagePercentTarget) break;

            var thumbsDir = Path.Combine(storageRoot, $"cam-{c.CameraId}", "thumbs");
            var snapshotsDir = Path.Combine(storageRoot, $"cam-{c.CameraId}", "snapshots");
            var archiveCameraMainDir = archiveRoot is null ? "" : Path.Combine(archiveRoot, $"cam-{c.CameraId}", "main");
            var archiveThisCamera = archiveEnabledByCamera.GetValueOrDefault(c.CameraId) && archiveUsable;

            // Prefer moving the file to the archive volume over deleting it — "footage must not be
            // deleted while there's another location for it". Falls back to a hard delete when
            // archiving is off / the archive is full / a per-file archive move fails.
            if (ArchiveOrDelete(c.File, c.MainDir, thumbsDir, snapshotsDir, archiveCameraMainDir, archiveThisCamera, archiveRoot, deletedPaths, relocations))
                projectedFreed += c.File.Length;
        }
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
    private async Task ImportOrphanedSegmentsAsync(NodeConfigResponse config, string storageRoot, string? archiveRoot,
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

            // If archiving is on, a primary file whose row has already been moved to the archive path
            // (a relocate report that landed, then a source-delete that didn't — a crash between the
            // two) would otherwise be re-imported here as a brand new segment. Skip it when its
            // archive-equivalent path is already a known row.
            var archiveCameraMainDir = archiveRoot is null ? null : Path.Combine(archiveRoot, $"cam-{camera.CameraId}", "main");
            toImport.AddRange(SelectImportableSegments(camera.CameraId, files, known, cameraDir, archiveCameraMainDir));
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

    /// <summary>Catches Segment rows still marked StorageTier=Primary whose file actually sits under
    /// this node's archive root — the case where an admin repoints a node's storage root to a new
    /// volume and demotes the old one to the archive root, so footage the node never explicitly
    /// moved is now on the archive volume. Fetches this node's Primary-tiered paths, walks the
    /// archive root's cam-{id}/main directories, and reports the intersection as tier-flips
    /// (OldFilePath == NewFilePath). Rides the normal relocation report; the end-of-sweep flush skips
    /// the source-delete when old == new. Nothing is deleted here, so it needs no reachability
    /// paranoia beyond the archiveUsable gate the caller already applies.</summary>
    private async Task<bool> ReconcileArchiveTierAsync(NodeConfigResponse config, string archiveRoot,
        List<PendingRelocation> relocations, CancellationToken ct)
    {
        List<string> primaryPaths;
        try
        {
            primaryPaths = await api.GetPrimaryTieredSegmentFilePathsAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Archive-tier reconciliation could not fetch this node's primary-tiered segment paths — will retry next sweep.");
            return false;
        }
        if (primaryPaths.Count == 0) return true;
        var primary = new HashSet<string>(primaryPaths, StringComparer.OrdinalIgnoreCase);

        var archiveMainDirs = config.Cameras
            .Select(c => Path.Combine(archiveRoot, $"cam-{c.CameraId}", "main"))
            .Concat(FindOrphanedCameraMainDirs(archiveRoot, config.Cameras.Select(c => c.CameraId)).Select(x => x.MainDir))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var found = 0;
        foreach (var mainDir in archiveMainDirs)
        {
            List<string> files;
            try { files = Directory.EnumerateFiles(mainDir, "*.mp4", SearchOption.AllDirectories).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var path in files)
            {
                if (!primary.Contains(path)) continue;
                long length; try { length = new FileInfo(path).Length; } catch { continue; }
                // Tier flip only — OldFilePath == NewFilePath, so the end-of-sweep flush skips the
                // source move/delete entirely; the cache-dir fields go unused.
                relocations.Add(new PendingRelocation(
                    new LarisVMS.Core.Dtos.SegmentRelocateItem(path, path, length), mainDir, "", "", "", ""));
                found++;
            }
        }

        if (found > 0)
            logger.LogInformation(
                "Archive-tier reconciliation: {Count} segment(s) whose file is on the archive volume but were still marked as primary storage — correcting.", found);
        return true;
    }

    private async Task<bool> ReconcileAsync(NodeConfigResponse config, string storageRoot, string? archiveRoot,
        HashSet<string> relocatingOldPaths, List<string> deletedPaths, CancellationToken ct)
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

        // A segment mid-relocation: its source is deliberately still on the primary volume (so
        // File.Exists says "present"), but its row is (or is about to be) at the archive path.
        // Excluded here so a stalled relocate report is never read as a deletion.
        if (relocatingOldPaths.Count > 0)
            knownPaths = knownPaths.Where(p => !relocatingOldPaths.Contains(p)).ToList();

        // The import direction only ever walks {storageRoot}/cam-{id}/main and can't lose data, so
        // the reachability probe alone is enough for it.
        if (!StorageHealth.CanReachStorage(storageRoot))
        {
            logger.LogWarning(
                "Reconciliation sweep skipped — storage root {StorageRoot} did not pass a read/write probe, so a missing file can't be told apart from an unreachable share. Will retry next sweep.",
                storageRoot);
            return false;
        }
        await ImportOrphanedSegmentsAsync(config, storageRoot, archiveRoot, knownPaths, ct);

        // A known path can live on either volume — gate the "missing -> delete the row" inference on
        // the reachability of whichever volume it's on, so an archive-share outage never deletes rows
        // for footage still on the primary and vice versa. Paths under neither root (a stale
        // storage-root change) are left alone entirely.
        var (primary, archive, _) = PartitionByRoot(knownPaths, storageRoot, archiveRoot);

        var ok = await ReconcileMissingSubsetAsync("primary storage", storageRoot, primary, storageReachableAlready: true, deletedPaths, ct);
        if (archive.Count > 0 && !string.IsNullOrWhiteSpace(archiveRoot))
            ok &= await ReconcileMissingSubsetAsync("archive storage", archiveRoot!, archive, storageReachableAlready: false, deletedPaths, ct);

        return ok;
    }

    /// <summary>The guarded "these known paths no longer exist on disk, so report their rows for
    /// deletion" flow, per storage volume: prove the volume is reachable, re-check every candidate
    /// after a short delay, and refuse a mass disappearance that looks more like a fault than real
    /// deletions. Returns false (caller retries sooner than the next scheduled reconcile) when the
    /// volume can't be trusted; true when it completed, whether or not anything was reported.</summary>
    private async Task<bool> ReconcileMissingSubsetAsync(string label, string root, List<string> known,
        bool storageReachableAlready, List<string> deletedPaths, CancellationToken ct)
    {
        if (known.Count == 0) return true;

        if (!storageReachableAlready && !StorageHealth.CanReachStorage(root))
        {
            logger.LogWarning("Reconciliation sweep skipped for {Label} — {Root} did not pass a read/write probe. Will retry next sweep.", label, root);
            return false;
        }

        var missing = SelectMissingPaths(known);
        if (missing.Count == 0) return true;

        try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
        catch (OperationCanceledException) { return false; }

        if (!StorageHealth.CanReachStorage(root))
        {
            logger.LogWarning("Reconciliation sweep abandoned for {Label} — {Root} stopped responding while re-verifying {Count} candidate(s). Will retry next sweep.", label, root, missing.Count);
            return false;
        }

        var confirmed = SelectMissingPaths(missing);
        if (confirmed.Count < missing.Count)
            logger.LogWarning("Reconciliation sweep ({Label}): {Recovered} of {Candidates} candidate(s) reappeared on re-check — storage was briefly unavailable. Only confirmed deletions are being reported.",
                label, missing.Count - confirmed.Count, missing.Count);
        if (confirmed.Count == 0) return true;

        if (StorageHealth.IsImplausibleMissingCount(confirmed.Count, known.Count))
        {
            logger.LogError(
                "Reconciliation sweep refusing to report {Count} of {Total} {Label} segment(s) as deleted — that proportion indicates a storage fault, not real deletions. Nothing has been reported; investigate the storage backend.",
                confirmed.Count, known.Count, label);
            return false;
        }

        logger.LogInformation("Reconciliation sweep found {Count} {Label} segment row(s) (of {Total} checked) pointing at files no longer on disk — reporting for cleanup.",
            confirmed.Count, label, known.Count);
        deletedPaths.AddRange(confirmed);
        return true;
    }

    /// <summary>Pure selection extracted from ReconcileAsync so it's testable against a real temp
    /// directory the same way EnumerateEvictable is, without a network round trip.</summary>
    internal static List<string> SelectMissingPaths(IEnumerable<string> knownPaths)
        => knownPaths.Where(p => !File.Exists(p)).ToList();

    // Pass 2c: {segment-stem}_span{spanId}.{jpg|webp} — the same naming convention Program.cs's own
    // snapshot-cache writer uses (WebP for new files, .jpg for ones already on disk).
    private static readonly System.Text.RegularExpressions.Regex SnapshotSpanIdPattern =
        new(@"_span(\d+)\.(?:jpg|webp)$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

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

    /// <summary>Pass G: staged eager-crop files (<c>snapshots/hires/{ticks}.{webp|jpg}</c>) older
    /// than a few days — bounded at the camera's own retention window, capped at 7 days. Tick-named,
    /// so a filename that doesn't parse as a positive Int64 is left alone. Pure/testable like the
    /// other selectors here.</summary>
    internal static List<string> SelectExpiredStagedCrops(string snapshotsDir, DateTime now, int? retentionDays)
    {
        var hiresDir = Path.Combine(snapshotsDir, "hires");
        if (!Directory.Exists(hiresDir)) return [];

        var maxAgeDays = retentionDays is > 0 ? Math.Min(retentionDays.Value, 7) : 7;
        var cutoff = now.AddDays(-maxAgeDays);

        var result = new List<string>();
        foreach (var file in LarisVMS.Media.CachedImageFormat.EnumerateFiles(hiresDir, "*", SearchOption.TopDirectoryOnly))
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
                .SelectMany(snapshotsDir => LarisVMS.Media.CachedImageFormat.EnumerateFiles(snapshotsDir, "*_span*", SearchOption.AllDirectories))
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
        HashSet<string> knownPaths, string? primaryMainDir = null, string? archiveMainDir = null)
    {
        var result = new List<SegmentReportItem>();
        foreach (var file in files)
        {
            if (knownPaths.Contains(file.FullName)) continue;
            // A file whose row has already moved to the archive path (a landed relocate report whose
            // source-delete then didn't happen) must not be re-imported as a new segment.
            if (primaryMainDir is not null && archiveMainDir is not null
                && knownPaths.Contains(BuildArchiveTargetPath(primaryMainDir, archiveMainDir, file.FullName)))
                continue;
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
    /// every bucketed-offset variant ("_o00...", "_o05...", ...) a hover request may have generated
    /// for it, in either cache format. Pure/testable against a real temp directory, same pattern as
    /// EnumerateEvictable.</summary>
    internal static List<string> FindMatchingThumbnails(string mainDir, string thumbsDir, string mainFilePath)
    {
        if (!Directory.Exists(thumbsDir)) return [];
        var relative = Path.GetRelativePath(mainDir, mainFilePath);
        var relativeDir = Path.GetDirectoryName(relative) ?? "";
        var stem = Path.GetFileNameWithoutExtension(relative);
        var thumbDirForSegment = Path.Combine(thumbsDir, relativeDir);
        if (!Directory.Exists(thumbDirForSegment)) return [];
        return LarisVMS.Media.CachedImageFormat.EnumerateFiles(thumbDirForSegment, stem + "_o*", SearchOption.TopDirectoryOnly).ToList();
    }

    private void DeleteMatchingThumbnails(string mainDir, string thumbsDir, string mainFilePath)
    {
        foreach (var thumb in FindMatchingThumbnails(mainDir, thumbsDir, mainFilePath)) TryDelete(thumb);
    }

    /// <summary>Object detection plan decision 10: every cached snapshot-image file belonging to
    /// mainFilePath — same derivation as FindMatchingThumbnails, but globbing "_span*" (one
    /// MotionSpan's own id) instead of "_o*" (a bucketed time offset), since a segment can carry
    /// more than one detected object, each with its own owning span and its own cached crop.</summary>
    internal static List<string> FindMatchingSnapshotImages(string mainDir, string snapshotsDir, string mainFilePath)
    {
        if (!Directory.Exists(snapshotsDir)) return [];
        var relative = Path.GetRelativePath(mainDir, mainFilePath);
        var relativeDir = Path.GetDirectoryName(relative) ?? "";
        var stem = Path.GetFileNameWithoutExtension(relative);
        var snapshotDirForSegment = Path.Combine(snapshotsDir, relativeDir);
        if (!Directory.Exists(snapshotDirForSegment)) return [];
        return LarisVMS.Media.CachedImageFormat.EnumerateFiles(snapshotDirForSegment, stem + "_span*", SearchOption.TopDirectoryOnly).ToList();
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

    // ── Archive storage (phase 2) — pure helpers, unit-tested against real temp dirs like the
    //    selectors above; wiring into SweepAsync is separate. ──────────────────────────────────

    /// <summary>Where a segment on the primary volume lands on the archive volume — the same
    /// cam-{id}/main/YYYY/MM/DD/HH/filename relative subpath, just under the archive root's
    /// cam-{id}/main. Keeping the layout identical means the node's media-path checks and the
    /// Path.GetRelativePath(mainDir, …) thumbnail/snapshot derivation work against either root with
    /// only a "which root matched" change.</summary>
    internal static string BuildArchiveTargetPath(string primaryCameraMainDir, string archiveCameraMainDir, string sourceFullPath)
        => Path.Combine(archiveCameraMainDir, Path.GetRelativePath(primaryCameraMainDir, sourceFullPath));

    /// <summary>True when <paramref name="candidate"/> is the same as or nested under
    /// <paramref name="root"/> (case-insensitive, separator-aware). The archive root must NOT be under
    /// the storage root, or the node's orphan-import sweep would re-import archived files as new
    /// primary segments.</summary>
    internal static bool IsUnderStorageRoot(string? candidate, string? root)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(root)) return false;
        var c = candidate.TrimEnd('/', '\\');
        var r = root.TrimEnd('/', '\\');
        return c.Equals(r, StringComparison.OrdinalIgnoreCase)
            || c.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || c.StartsWith(r + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Splits a set of file paths into (under storageRoot, under archiveRoot, neither) so
    /// ReconcileAsync can gate a "missing file" inference on the reachability of whichever volume the
    /// path lives on — an archive-share outage must not delete rows for footage still on primary and
    /// vice versa.</summary>
    internal static (List<string> Primary, List<string> Archive, List<string> Other) PartitionByRoot(
        IEnumerable<string> paths, string storageRoot, string? archiveRoot)
    {
        List<string> primary = [], archive = [], other = [];
        foreach (var p in paths)
        {
            if (!string.IsNullOrWhiteSpace(archiveRoot) && IsUnderStorageRoot(p, archiveRoot)) archive.Add(p);
            else if (IsUnderStorageRoot(p, storageRoot)) primary.Add(p);
            else other.Add(p);
        }
        return (primary, archive, other);
    }

    /// <summary>Moves one segment file onto the archive volume: copy to a same-volume ".tmp", verify
    /// size, atomically rename, then stamp the archive copy's mtime to the source's so the archive
    /// expiry pass measures age from the recording time. Returns true on success (the SOURCE is left
    /// in place — the caller deletes it only after the web has recorded the new path). Idempotent: a
    /// target already present with matching size is treated as done. Any I/O failure leaves the
    /// source untouched and the ".tmp" cleaned up, and the move retries next sweep.</summary>
    internal static bool TryArchiveFile(string sourceFullPath, string targetFullPath, ILogger logger)
    {
        try
        {
            var source = new FileInfo(sourceFullPath);
            if (!source.Exists) return false;

            var targetDir = Path.GetDirectoryName(targetFullPath);
            if (targetDir is not null) Directory.CreateDirectory(targetDir);

            if (File.Exists(targetFullPath) && new FileInfo(targetFullPath).Length == source.Length)
                return true; // a previous sweep already copied it; the report just hadn't landed

            var tmp = targetFullPath + ".tmp";
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* stale, best effort */ }

            File.Copy(sourceFullPath, tmp, overwrite: true);
            if (new FileInfo(tmp).Length != source.Length)
            {
                try { File.Delete(tmp); } catch { /* best effort */ }
                logger.LogWarning("Archive copy of {Source} was a different size than the source — leaving it on primary, will retry.", sourceFullPath);
                return false;
            }

            if (File.Exists(targetFullPath)) File.Delete(targetFullPath);
            File.Move(tmp, targetFullPath);
            try { File.SetLastWriteTimeUtc(targetFullPath, source.LastWriteTimeUtc); } catch { /* mtime is a best-effort anchor */ }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not archive {Source} — leaving it on primary, will retry next sweep.", sourceFullPath);
            try { if (File.Exists(targetFullPath + ".tmp")) File.Delete(targetFullPath + ".tmp"); } catch { /* best effort */ }
            return false;
        }
    }

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
