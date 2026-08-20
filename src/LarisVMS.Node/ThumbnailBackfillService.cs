using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LarisVMS.Node;

/// <summary>
/// M7 pass 2: low-priority background catch-up for hover thumbnails. Generation is otherwise
/// entirely on-demand (the first hover into a bucket triggers it via NodeWorker.CaptureThumbnailAsync)
/// — this exists only so a camera with a long, never-hovered history isn't permanently slow on its
/// very first hover anywhere, and so a fast sweep across a long-uncached stretch of timeline doesn't
/// have to wait on ffmpeg one bucket at a time. Purely filesystem-driven, same as StorageManager
/// (LarisVMS.Node has no DB connection) — walks each camera's own 5-minute-aligned segments (the
/// ones TimelineService.GetThumbnailInfoAsync would actually select, offset 0) and fills in whatever
/// the cache is missing, a few at a time via NodeWorker's own low-priority gate and BelowNormal OS
/// process priority, with a pause between each so this never meaningfully competes with live
/// recording or an on-demand hover. Polls slowly once a full pass finds nothing left to do, rather
/// than continuously re-walking every segment on disk.
/// </summary>
public class ThumbnailBackfillService(NodeApiClient api, NodeWorker worker, string fallbackStorageRoot,
    ILogger<ThumbnailBackfillService> logger) : BackgroundService
{
    private static readonly TimeSpan CaughtUpPollInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BehindRetryDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan BetweenEachDelay = TimeSpan.FromSeconds(2);

    // Bucket size mirrors TimelineService.ThumbnailBucketSeconds exactly — both sides must agree on
    // what "aligned" means, or this would backfill offsets GetThumbnailInfoAsync would never
    // actually request. 5-minute-boundary alignment is timezone-offset-invariant (every real-world
    // UTC offset is itself a multiple of 5 minutes), so this checks the segment filename's own
    // wall-clock minute/second directly — no UTC conversion needed, which matters here since the
    // filename encodes local time (RecordingSession's -strftime pattern) while TimelineService
    // buckets against the database's true UTC StartUtc; both land on the same aligned instants.
    private const int BucketMinutes = 5;

    // Segments this fresh are skipped — ffmpeg's segment muxer may still be finalizing one whose
    // filename already exists but whose bytes aren't fully flushed yet. Smaller than StorageManager's
    // own 5-minute MinAge (that guards against evicting a segment still being written; this only
    // needs to wait out one segment rotation, ~60s by default).
    private static readonly TimeSpan MinAge = TimeSpan.FromMinutes(2);

    // Caps how many a single pass processes before yielding back to the outer loop — keeps each
    // pass's own directory walk plus the outer loop's cancellation check responsive on a node with a
    // very large backlog, rather than one pass silently running for a long time uninterrupted.
    private const int MaxPerPass = 20;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool caughtUp;
            try
            {
                caughtUp = await BackfillOnceAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Thumbnail backfill pass failed — will retry.");
                caughtUp = false;
            }

            try { await Task.Delay(caughtUp ? CaughtUpPollInterval : BehindRetryDelay, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Returns true only if this pass found nothing left to backfill anywhere — the caller
    /// then backs off to the slow poll interval instead of immediately re-walking every camera's
    /// directory tree again.</summary>
    private async Task<bool> BackfillOnceAsync(CancellationToken ct)
    {
        var config = await api.GetConfigAsync(ct);
        var storageRoot = string.IsNullOrWhiteSpace(config.StorageRootPath) ? fallbackStorageRoot : config.StorageRootPath;
        if (!Directory.Exists(storageRoot)) return true;

        var now = DateTime.UtcNow;
        var processed = 0;

        foreach (var camera in config.Cameras)
        {
            var mainDir = Path.Combine(storageRoot, $"cam-{camera.CameraId}", "main");
            if (!Directory.Exists(mainDir)) continue;
            var thumbsDir = Path.Combine(storageRoot, $"cam-{camera.CameraId}", "thumbs");

            foreach (var segmentPath in FindAlignedSegmentsMissingThumbnails(mainDir, thumbsDir, now))
            {
                if (ct.IsCancellationRequested) return false;
                if (processed >= MaxPerPass) return false;

                var bytes = await worker.CaptureThumbnailInBackgroundAsync(segmentPath, 0, ct);
                if (bytes is not null)
                {
                    var relative = Path.GetRelativePath(mainDir, segmentPath);
                    var thumbPath = Path.Combine(thumbsDir, Path.ChangeExtension(relative, null) + $"_o00_{LarisVMS.Media.ThumbnailCapture.DefaultMaxDimension}.jpg");
                    await LarisVMS.Media.ThumbnailCapture.SaveToCacheAsync(thumbPath, bytes, ct);
                }
                processed++;

                try { await Task.Delay(BetweenEachDelay, ct); }
                catch (OperationCanceledException) { return false; }
            }
        }

        return processed == 0;
    }

    /// <summary>Pure enumeration extracted so it's testable against a real temp directory the same
    /// way StorageManager.EnumerateEvictable is — every segment under mainDir whose own filename
    /// timestamp falls on a BucketMinutes boundary and old enough to trust, that doesn't already have
    /// a matching offset-0 thumbnail in thumbsDir.</summary>
    internal static IEnumerable<string> FindAlignedSegmentsMissingThumbnails(string mainDir, string thumbsDir, DateTime nowUtc)
    {
        foreach (var path in Directory.EnumerateFiles(mainDir, "*.mp4", SearchOption.AllDirectories))
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            if (!DateTime.TryParseExact(stem, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.None, out var wallClock))
                continue;
            if (wallClock.Minute % BucketMinutes != 0 || wallClock.Second != 0) continue;

            FileInfo info;
            try { info = new FileInfo(path); }
            catch (IOException) { continue; }
            if (nowUtc - info.LastWriteTimeUtc < MinAge) continue;

            var relative = Path.GetRelativePath(mainDir, path);
            var thumbPath = Path.Combine(thumbsDir, Path.ChangeExtension(relative, null) + $"_o00_{LarisVMS.Media.ThumbnailCapture.DefaultMaxDimension}.jpg");
            if (!File.Exists(thumbPath)) yield return path;
        }
    }
}
