using LarisVMS.Core.Dtos;

namespace LarisVMS.Core.Interfaces;

/// <summary>M7 playback/timeline: coverage buckets for the canvas timeline, the segment list a
/// player scrubs across, and the lookup the /playback-segment proxy needs to reach the owning
/// node. Read-only — recording/deletion stay owned by CameraService/NodeService.</summary>
public interface ITimelineService
{
    /// <summary>Buckets [fromUtc, toUtc) into bucketCount equal spans and reports whether any
    /// segment overlaps each one. One query for every segment touching the range, then bucketed
    /// in memory — simpler and fewer round trips than one query per bucket.</summary>
    Task<List<TimelineBucketDto>> GetBucketsAsync(Guid cameraId, DateTime fromUtc, DateTime toUtc, int bucketCount, CancellationToken ct = default);

    /// <summary>Same bucketing as GetBucketsAsync but merged across a set of cameras instead of
    /// just one — a bucket is recorded (recording and/or motion) if *any* of them has it there.
    /// cameraIds null or empty means every camera (the original M7 "was anything recording
    /// anywhere" behavior) — Pages/Playback now always passes the current view's own camera set,
    /// so the merged timeline only ever reflects what's actually on screen, not every camera in the
    /// system.</summary>
    Task<List<TimelineBucketDto>> GetGlobalBucketsAsync(DateTime fromUtc, DateTime toUtc, int bucketCount, IReadOnlyList<Guid>? cameraIds = null, CancellationToken ct = default);

    /// <summary>M8: camera IDs with a MotionSpan row recent enough to still count as "motion is
    /// active right now" — the Live-view indicator's signal. Deliberately derived from the same
    /// MotionSpans table the timeline already reads, not a live round-trip to each camera's node:
    /// NodeWorker checkpoints an open span into this table every ~15s (see
    /// NodeWorker.EnqueueMotionCheckpoints), so a recent row already means "still going" without a
    /// new proxy/auth path to the node. See TimelineService's implementation for the staleness
    /// window this checks against.</summary>
    Task<List<Guid>> GetCamerasWithActiveMotionAsync(CancellationToken ct = default);

    /// <summary>The same recent-activity query narrowed to spans carrying a detected object class,
    /// grouped per camera and already resolved to display form — what lets a live tile show
    /// "🚶 Person" instead of only the generic motion badge. A camera with recent motion but no
    /// classified detection simply doesn't appear.</summary>
    Task<List<CameraDetectionStateDto>> GetActiveDetectionsAsync(CancellationToken ct = default);

    /// <summary>Every segment overlapping [fromUtc, toUtc), ordered by start — what a player
    /// resolves "which file covers this instant" against.</summary>
    Task<List<SegmentSummaryDto>> GetSegmentsAsync(Guid cameraId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>Null if segmentId doesn't exist or doesn't belong to cameraId — the /playback-segment
    /// proxy treats that as 404 rather than trusting the caller's cameraId/segmentId pairing.</summary>
    Task<PlaybackSegmentInfo?> GetSegmentForPlaybackAsync(Guid cameraId, long segmentId, CancellationToken ct = default);

    /// <summary>Every segment's FilePath (plus the node it actually lives on) overlapping [fromUtc,
    /// toUtc) for one camera, ordered by StartUtc — sibling to GetSegmentsAsync (which returns
    /// Id/StartUtc/EndUtc for the timeline player) rather than an extension of it: ExportJobDispatcher
    /// needs an ordered list of raw paths to write into its ffmpeg concat list file, not display
    /// metadata. NodeId is included (not just FilePath) so the dispatcher can detect a range that
    /// crosses a camera's reassignment from one node to another — a segment recorded before the move
    /// still lives on the old node's disk, not the camera's current one.</summary>
    Task<List<SegmentFileInfo>> GetSegmentFilePathsAsync(Guid cameraId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>M7 pass 2 (hover thumbnails): buckets atUtc down to the nearest 5 minutes, then
    /// resolves the one segment covering that bucketed instant (point containment, not
    /// GetSegmentsAsync's range-overlap predicate) and the owning node's connection info, the same
    /// way GetSegmentForPlaybackAsync does for a segment id. Null if no segment covers the bucketed
    /// instant (a gap — camera offline, motion-mode not recording then). OffsetSeconds on the
    /// returned info is clamped short of the segment's own end; the /playback-thumbnail proxy issues
    /// its token and the node names its cache file from this one canonical value.</summary>
    Task<ThumbnailInfo?> GetThumbnailInfoAsync(Guid cameraId, DateTime atUtc, CancellationToken ct = default);

    /// <summary>M18: same lookup as GetThumbnailInfoAsync but resolved against the exact requested
    /// instant rather than snapped to the nearest 5 minutes — see the implementation's own doc
    /// comment for why the Snapshots browser needs this instead of the bucketed version.</summary>
    Task<ThumbnailInfo?> GetExactThumbnailInfoAsync(Guid cameraId, DateTime atUtc, CancellationToken ct = default);

    /// <summary>Dashboard's "most recent thumbnail" column: the newest completed segment's own
    /// last frame — see the implementation's own doc comment for why this is deliberately not
    /// GetThumbnailInfoAsync (bucketed/historical) or the live-RTSP snapshot endpoint (too heavy to
    /// poll per-camera every dashboard refresh). Null if this camera has no segments yet.</summary>
    Task<ThumbnailInfo?> GetLatestThumbnailInfoAsync(Guid cameraId, CancellationToken ct = default);

    /// <summary>M18: the Snapshots browser's own query — every MotionSpan (any Source), newest
    /// first, narrowed by an optional camera and/or date range, server-side paged. See SnapshotDto's
    /// own doc comment for why this reuses MotionSpans directly rather than a new table: every motion
    /// event is already "tagged" with a zone/rule/detection class, and the image itself is extracted
    /// from the recording on demand, not captured or stored separately.
    ///
    /// kinds is the page's own event-type filter (checkboxes: "Motion", "CustomTag", or a
    /// DetectionKind name — see TimelineService.CustomTagKindToken), a pure narrowing on top of
    /// whatever the admin-level SnapshotVisibility setting already allows. Null/empty means no
    /// additional narrowing.</summary>
    Task<SnapshotPageDto> GetSnapshotsAsync(Guid? cameraId, DateTime? fromUtc, DateTime? toUtc, int page, int pageSize, CancellationToken ct = default, IReadOnlyCollection<string>? kinds = null);
}
