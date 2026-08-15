using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

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

        // M8 pass 8: ColorHex comes along via the (optional) EventTagRule navigation — null for a
        // built-in or ServerMotion-zone span, same as MotionSpan.EventTagRuleId itself being null.
        // EF translates the null-conditional into a left join, so a span with no rule still comes
        // back (just with ColorHex null) rather than being excluded.
        var motionSpans = await db.MotionSpans
            .Where(m => m.CameraId == cameraId && m.StartUtc < toUtc && m.EndUtc > fromUtc)
            .Select(m => new { m.StartUtc, m.EndUtc, ColorHex = m.EventTagRule != null ? m.EventTagRule.ColorHex : null })
            .ToListAsync(ct);

        return Bucket(segments.Select(s => (s.StartUtc, s.EndUtc)),
            motionSpans.Select(m => (m.StartUtc, m.EndUtc, m.ColorHex)), fromUtc, toUtc, bucketCount);
    }

    public async Task<List<TimelineBucketDto>> GetGlobalBucketsAsync(DateTime fromUtc, DateTime toUtc, int bucketCount, IReadOnlyList<Guid>? cameraIds = null, CancellationToken ct = default)
    {
        fromUtc = NormalizeToUtc(fromUtc);
        toUtc = NormalizeToUtc(toUtc);

        // Same shape as GetBucketsAsync, just merged across cameraIds instead of scoped to one —
        // a bucket only needs "did *any* segment among these cameras overlap it", so which specific
        // one it belonged to is irrelevant past this point and isn't even selected. A null/empty
        // cameraIds is the original "every camera in the system" behavior — Pages/Playback no
        // longer relies on that default (it always passes the current view's own set), but it's
        // kept as the fallback rather than made a hard requirement, since "merged across
        // everything" is still a reasonable answer for a caller that genuinely has no camera scope.
        // Built as a conditionally-attached .Where rather than an inline "noFilter || cameraIds!
        // .Contains(...)" — the latter still puts a Contains call over a possibly-null cameraIds
        // into the expression tree EF has to translate even on the branch that's logically never
        // taken, which risks exactly the null-reference EF query translation is prone to for that
        // pattern. A plain if only ever builds the Contains call when cameraIds is actually there.
        var segmentsQuery = db.Segments.Where(s => s.StartUtc < toUtc && s.EndUtc > fromUtc);
        var motionQuery = db.MotionSpans.Where(m => m.StartUtc < toUtc && m.EndUtc > fromUtc);
        if (cameraIds is { Count: > 0 })
        {
            segmentsQuery = segmentsQuery.Where(s => cameraIds.Contains(s.CameraId));
            motionQuery = motionQuery.Where(m => cameraIds.Contains(m.CameraId));
        }

        var segments = await segmentsQuery
            .Select(s => new { s.StartUtc, s.EndUtc })
            .ToListAsync(ct);

        var motionSpans = await motionQuery
            .Select(m => new { m.StartUtc, m.EndUtc, ColorHex = m.EventTagRule != null ? m.EventTagRule.ColorHex : null })
            .ToListAsync(ct);

        return Bucket(segments.Select(s => (s.StartUtc, s.EndUtc)),
            motionSpans.Select(m => (m.StartUtc, m.EndUtc, m.ColorHex)), fromUtc, toUtc, bucketCount);
    }

    private static List<TimelineBucketDto> Bucket(
        IEnumerable<(DateTime StartUtc, DateTime EndUtc)> segments,
        IEnumerable<(DateTime StartUtc, DateTime EndUtc, string? ColorHex)> motionSpans,
        DateTime fromUtc, DateTime toUtc, int bucketCount)
    {
        if (bucketCount < 1) bucketCount = 1;
        if (toUtc <= fromUtc) return [];

        var segmentList = segments as ICollection<(DateTime StartUtc, DateTime EndUtc)> ?? segments.ToList();
        var motionList = motionSpans as ICollection<(DateTime StartUtc, DateTime EndUtc, string? ColorHex)> ?? motionSpans.ToList();

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
            var hasMotion = motionList.Any(m => m.StartUtc < bucketEnd && m.EndUtc > bucketStart);
            // M8 pass 8: the earliest-starting overlapping custom-tag span wins this bucket's color
            // (ties broken by ColorHex text so the pick is at least deterministic, not "whichever the
            // query happened to return first") — a bucket straddling two different tag types is rare
            // (they'd need to genuinely overlap in time) and picking one over blending keeps every
            // bucket a single solid color, same reasoning as the motion/recording stripe-merge fix.
            var tagColorHex = motionList
                .Where(m => m.ColorHex is not null && m.StartUtc < bucketEnd && m.EndUtc > bucketStart)
                .OrderBy(m => m.StartUtc).ThenBy(m => m.ColorHex, StringComparer.Ordinal)
                .Select(m => m.ColorHex)
                .FirstOrDefault();
            buckets.Add(new TimelineBucketDto(bucketStart, bucketEnd, hasRecording, hasMotion, tagColorHex));
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

    // Coarse on purpose (user-specified): enough to notice something changed while hovering,
    // without generating/caching/storing a thumbnail every few seconds. Segments are clock-aligned
    // (segment_atclocktime=1, 60s each), so a 5-minute-boundary instant normally lands exactly on
    // some segment's own start — offsetSeconds below is almost always 0 in practice.
    private const int ThumbnailBucketSeconds = 300;

    public async Task<ThumbnailInfo?> GetThumbnailInfoAsync(Guid cameraId, DateTime atUtc, CancellationToken ct = default)
    {
        atUtc = NormalizeToUtc(atUtc);
        var bucketTicks = TimeSpan.FromSeconds(ThumbnailBucketSeconds).Ticks;
        var bucketedUtc = new DateTime((atUtc.Ticks / bucketTicks) * bucketTicks, DateTimeKind.Utc);

        var segment = await db.Segments
            .Where(s => s.CameraId == cameraId && s.StartUtc <= bucketedUtc && s.EndUtc > bucketedUtc)
            .Select(s => new { s.FilePath, s.NodeId, s.StartUtc, s.DurationMs })
            .FirstOrDefaultAsync(ct);
        if (segment is null) return null;

        var node = await db.Nodes
            .Where(n => n.Id == segment.NodeId)
            .Select(n => new { n.LastIpAddress, n.LivePort, n.MediaSigningKey })
            .FirstOrDefaultAsync(ct);

        // Clamped short of the segment's own end — an offset landing exactly on/past EndUtc would
        // ask ffmpeg to seek past the last frame this segment actually has.
        var rawOffsetSeconds = (int)(bucketedUtc - segment.StartUtc).TotalSeconds;
        var maxOffsetSeconds = Math.Max(0, segment.DurationMs / 1000 - 1);
        var offsetSeconds = Math.Clamp(rawOffsetSeconds, 0, maxOffsetSeconds);

        return new ThumbnailInfo(segment.FilePath, offsetSeconds, node?.LastIpAddress, node?.LivePort, node?.MediaSigningKey);
    }

    public async Task<List<SegmentFileInfo>> GetSegmentFilePathsAsync(Guid cameraId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        fromUtc = NormalizeToUtc(fromUtc);
        toUtc = NormalizeToUtc(toUtc);

        return await db.Segments
            .Where(s => s.CameraId == cameraId && s.StartUtc < toUtc && s.EndUtc > fromUtc)
            .OrderBy(s => s.StartUtc)
            .Select(s => new SegmentFileInfo(s.FilePath, s.NodeId))
            .ToListAsync(ct);
    }

    // NodeWorker checkpoints an open span roughly every 15s (see EnqueueMotionCheckpoints) — 25s
    // gives one checkpoint's worth of margin for the report round trip and Live's own poll interval
    // without stretching so far that a genuinely-just-ended span still reads as "active" for long
    // after it closed.
    private static readonly TimeSpan ActiveMotionStaleness = TimeSpan.FromSeconds(25);

    public async Task<List<Guid>> GetCamerasWithActiveMotionAsync(CancellationToken ct = default)
    {
        var threshold = DateTime.UtcNow - ActiveMotionStaleness;
        return await db.MotionSpans
            .Where(m => m.EndUtc >= threshold)
            .Select(m => m.CameraId)
            .Distinct()
            .ToListAsync(ct);
    }
}
