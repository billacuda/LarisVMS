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
    /// Defensive, not load-bearing for the current callers. An earlier version of this comment
    /// claimed ASP.NET's query-string binding turns "2026-08-08T21:45:31.890Z" into
    /// <c>Kind=Local</c> converted to the server's zone, and that this was the cause of playback
    /// never finding a segment. That was wrong on both counts: re-checked against a real running
    /// minimal API (not a hand-rolled DateTime.TryParse, which is what produced the bogus result),
    /// a Z-suffixed value binds as <c>Kind=Utc</c> directly, with no shift. Playback's actual bug
    /// was in the recorder's muxer flags — see RecordingSession.MseMovFlags.
    ///
    /// It stays because the conversion is still correct and cheap, and it does real work for any
    /// caller that supplies a bound without the trailing Z: SQL Server's datetime2 carries no
    /// offset, so EF would send whatever wall-clock number it was given straight into a comparison
    /// against UTC columns.
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
