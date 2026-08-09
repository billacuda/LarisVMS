using Microsoft.EntityFrameworkCore;
using NidusVMS.Core.Dtos;
using NidusVMS.Core.Interfaces;
using NidusVMS.Infrastructure.Data;

namespace NidusVMS.Infrastructure.Services;

public class TimelineService(ApplicationDbContext db) : ITimelineService
{
    /// <summary>
    /// Normalizes an incoming range bound to a true UTC value before it's compared against the
    /// database's UTC columns.
    ///
    /// This is load-bearing, not defensive tidying. ASP.NET's query-string binding parses
    /// "2026-08-08T21:45:31.890Z" into a DateTime with <c>Kind=Local</c>, *converted* to the
    /// server's local zone (confirmed: on a UTC-7 host that value binds as 14:45:31 Local). Since
    /// SQL Server's datetime2 carries no offset, EF then sends that wall-clock number as-is and
    /// every range query silently searches a window shifted by the server's UTC offset. That made
    /// playback look broken in exactly one direction: the timeline still rendered plausibly (its
    /// whole window shifts uniformly, so it just looks like different footage), but the segment
    /// lookup for a specific instant returned segments hours away from it, so no segment ever
    /// covered the requested time and no video ever loaded.
    ///
    /// Local → convert (recovers the original instant). Utc → already correct. Unspecified →
    /// stamp as UTC rather than assume local: every caller here is passing something that means
    /// UTC, and treating it as local would reintroduce the same offset bug for any client that
    /// omits the trailing Z.
    /// </summary>
    internal static DateTime NormalizeToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    public async Task<List<TimelineBucketDto>> GetBucketsAsync(Guid cameraId, DateTime fromUtc, DateTime toUtc, int bucketCount, CancellationToken ct = default)
    {
        fromUtc = NormalizeToUtc(fromUtc);
        toUtc = NormalizeToUtc(toUtc);

        // Pulled once, bucketed in memory below, rather than one query per bucket — a bucket
        // count in the hundreds (one canvas-timeline redraw) would otherwise be hundreds of
        // round trips for what's a small amount of data either way.
        var segments = await db.Segments
            .Where(s => s.CameraId == cameraId && s.StartUtc < toUtc && s.EndUtc > fromUtc)
            .Select(s => new { s.StartUtc, s.EndUtc })
            .ToListAsync(ct);

        return Bucket(segments.Select(s => (s.StartUtc, s.EndUtc)), fromUtc, toUtc, bucketCount);
    }

    public async Task<List<TimelineBucketDto>> GetGlobalBucketsAsync(DateTime fromUtc, DateTime toUtc, int bucketCount, CancellationToken ct = default)
    {
        fromUtc = NormalizeToUtc(fromUtc);
        toUtc = NormalizeToUtc(toUtc);

        // Same shape as GetBucketsAsync, just without the CameraId filter — a bucket only needs
        // "did *any* segment (any camera) overlap it", so which camera it belonged to is
        // irrelevant past this point and isn't even selected.
        var segments = await db.Segments
            .Where(s => s.StartUtc < toUtc && s.EndUtc > fromUtc)
            .Select(s => new { s.StartUtc, s.EndUtc })
            .ToListAsync(ct);

        return Bucket(segments.Select(s => (s.StartUtc, s.EndUtc)), fromUtc, toUtc, bucketCount);
    }

    private static List<TimelineBucketDto> Bucket(IEnumerable<(DateTime StartUtc, DateTime EndUtc)> segments, DateTime fromUtc, DateTime toUtc, int bucketCount)
    {
        if (bucketCount < 1) bucketCount = 1;
        if (toUtc <= fromUtc) return [];

        var segmentList = segments as ICollection<(DateTime StartUtc, DateTime EndUtc)> ?? segments.ToList();

        var totalTicks = (toUtc - fromUtc).Ticks;
        var bucketTicks = totalTicks / bucketCount;
        var buckets = new List<TimelineBucketDto>(bucketCount);

        for (var i = 0; i < bucketCount; i++)
        {
            var bucketStart = fromUtc.AddTicks(bucketTicks * i);
            // Last bucket absorbs any remainder from integer-dividing totalTicks, so buckets
            // always cover the full [fromUtc, toUtc) range with no gap at the end.
            var bucketEnd = i == bucketCount - 1 ? toUtc : fromUtc.AddTicks(bucketTicks * (i + 1));
            var hasRecording = segmentList.Any(s => s.StartUtc < bucketEnd && s.EndUtc > bucketStart);
            buckets.Add(new TimelineBucketDto(bucketStart, bucketEnd, hasRecording));
        }

        return buckets;
    }

    public async Task<List<SegmentSummaryDto>> GetSegmentsAsync(Guid cameraId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        // See NormalizeToUtc — this is the query whose silent offset shift kept playback from ever
        // finding a segment covering the requested instant.
        fromUtc = NormalizeToUtc(fromUtc);
        toUtc = NormalizeToUtc(toUtc);

        return await db.Segments
            .Where(s => s.CameraId == cameraId && s.StartUtc < toUtc && s.EndUtc > fromUtc)
            .OrderBy(s => s.StartUtc)
            .Select(s => new SegmentSummaryDto(s.Id, s.StartUtc, s.EndUtc))
            .ToListAsync(ct);
    }

    public async Task<PlaybackSegmentInfo?> GetSegmentForPlaybackAsync(Guid cameraId, long segmentId, CancellationToken ct = default)
    {
        var segment = await db.Segments
            .Where(s => s.Id == segmentId && s.CameraId == cameraId)
            .Select(s => new { s.FilePath, s.NodeId })
            .FirstOrDefaultAsync(ct);
        if (segment is null) return null;

        var node = await db.Nodes
            .Where(n => n.Id == segment.NodeId)
            .Select(n => new { n.LastIpAddress, n.LivePort, n.MediaSigningKey })
            .FirstOrDefaultAsync(ct);

        return new PlaybackSegmentInfo(segment.FilePath, node?.LastIpAddress, node?.LivePort, node?.MediaSigningKey);
    }
}
