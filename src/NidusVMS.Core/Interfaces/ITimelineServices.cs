using NidusVMS.Core.Dtos;

namespace NidusVMS.Core.Interfaces;

/// <summary>M7 playback/timeline: coverage buckets for the canvas timeline, the segment list a
/// player scrubs across, and the lookup the /playback-segment proxy needs to reach the owning
/// node. Read-only — recording/deletion stay owned by CameraService/NodeService.</summary>
public interface ITimelineService
{
    /// <summary>Buckets [fromUtc, toUtc) into bucketCount equal spans and reports whether any
    /// segment overlaps each one. One query for every segment touching the range, then bucketed
    /// in memory — simpler and fewer round trips than one query per bucket.</summary>
    Task<List<TimelineBucketDto>> GetBucketsAsync(Guid cameraId, DateTime fromUtc, DateTime toUtc, int bucketCount, CancellationToken ct = default);

    /// <summary>Same bucketing as GetBucketsAsync but merged across every camera, not just one —
    /// a bucket is recorded if *any* camera has footage there. No motion aggregation yet (same
    /// reason single-camera buckets have none: MotionSpans doesn't exist until M8) — once it does,
    /// this is where "motion on any camera" would be OR'd in the same way.</summary>
    Task<List<TimelineBucketDto>> GetGlobalBucketsAsync(DateTime fromUtc, DateTime toUtc, int bucketCount, CancellationToken ct = default);

    /// <summary>Every segment overlapping [fromUtc, toUtc), ordered by start — what a player
    /// resolves "which file covers this instant" against.</summary>
    Task<List<SegmentSummaryDto>> GetSegmentsAsync(Guid cameraId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>Null if segmentId doesn't exist or doesn't belong to cameraId — the /playback-segment
    /// proxy treats that as 404 rather than trusting the caller's cameraId/segmentId pairing.</summary>
    Task<PlaybackSegmentInfo?> GetSegmentForPlaybackAsync(Guid cameraId, long segmentId, CancellationToken ct = default);
}
