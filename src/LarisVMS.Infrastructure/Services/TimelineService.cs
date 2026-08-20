using Microsoft.EntityFrameworkCore;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

// settings is optional (defaults to null, resolved from DI in production regardless — see
// GetSnapshotsAsync's own comment) so the many existing tests constructing this directly with just
// (db, palette) keep compiling unchanged; only tests exercising the new type-filtering need to pass one.
public class TimelineService(ApplicationDbContext db, IEventColorService eventColors, ISettingsResolver? settings = null) : ITimelineService
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
            .Select(m => new
            {
                m.StartUtc,
                m.EndUtc,
                ColorHex = m.EventTagRule != null ? m.EventTagRule.ColorHex : null,
                m.DetectionKind
            })
            .ToListAsync(ct);

        var palette = await eventColors.GetAsync(ct);

        return Bucket(segments.Select(s => (s.StartUtc, s.EndUtc)),
            motionSpans.Select(m => (m.StartUtc, m.EndUtc, ResolveColor(palette, m.ColorHex, m.DetectionKind))),
            fromUtc, toUtc, bucketCount);
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
            .Select(m => new
            {
                m.StartUtc,
                m.EndUtc,
                ColorHex = m.EventTagRule != null ? m.EventTagRule.ColorHex : null,
                m.DetectionKind
            })
            .ToListAsync(ct);

        var palette = await eventColors.GetAsync(ct);

        return Bucket(segments.Select(s => (s.StartUtc, s.EndUtc)),
            motionSpans.Select(m => (m.StartUtc, m.EndUtc, ResolveColor(palette, m.ColorHex, m.DetectionKind))),
            fromUtc, toUtc, bucketCount);
    }

    /// <summary>A span's timeline color. A user-configured EventTagRule's own color still wins
    /// outright (it's an explicit choice, unlike an inferred object class); an object detection
    /// supplies its class color next — whatever an admin picked on Admin/Event Colors, or the
    /// built-in default for that class; anything else falls through to the plain motion/recording
    /// scheme by returning null. Reusing the existing TagColorHex channel rather than adding a second
    /// color field means the canvas renderer needs no new concept.</summary>
    private static string? ResolveColor(EventPalette palette, string? tagColorHex, DetectionKind? detectionKind)
        => tagColorHex ?? (detectionKind is { } kind ? palette.ColorFor(kind) : null);

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
            // Every distinct colour overlapping this bucket, not just one: object classes genuinely
            // co-occur (a person and a vehicle in the same instant), and picking a winner would hide
            // the rest. Ordered by start time, ties broken by the colour text so the banding is
            // stable frame to frame rather than reshuffling on each reload. The renderer draws one
            // horizontal band per colour; a single colour draws exactly as it always did.
            var tagColorHexes = motionList
                .Where(m => m.ColorHex is not null && m.StartUtc < bucketEnd && m.EndUtc > bucketStart)
                .OrderBy(m => m.StartUtc).ThenBy(m => m.ColorHex, StringComparer.Ordinal)
                .Select(m => m.ColorHex!)
                .Distinct()
                .ToList();
            buckets.Add(new TimelineBucketDto(bucketStart, bucketEnd, hasRecording, hasMotion,
                tagColorHexes.FirstOrDefault(),
                tagColorHexes.Count > 0 ? tagColorHexes : null));
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
        return await ResolveThumbnailInfoAsync(cameraId, bucketedUtc, ct);
    }

    /// <summary>M18: the Snapshots browser's own lookup — same segment/offset resolution as
    /// GetThumbnailInfoAsync, but against the exact requested instant instead of snapping to the
    /// nearest 5 minutes. GetThumbnailInfoAsync's bucketing exists to keep interactive scrub-hover
    /// cheap (many requests a second while dragging) and its own doc comment already admits it can
    /// read "up to ~10 minutes stale" — fine for a hover preview, wrong for a snapshot of one specific
    /// motion event: a motion-mode camera's segment covering the *bucketed* instant is exactly the
    /// kind of coverage gap a real event is likely to fall into (recording had just resumed, or
    /// hadn't yet, at the rounded-off mark), which read as "snapshots don't show anything real" when
    /// this reused the bucketed lookup. Snapshots renders at most one page (24) of images per load,
    /// nowhere near hover-scrub's request volume, so there's no cost concern in resolving each one
    /// exactly.</summary>
    public async Task<ThumbnailInfo?> GetExactThumbnailInfoAsync(Guid cameraId, DateTime atUtc, CancellationToken ct = default)
        => await ResolveThumbnailInfoAsync(cameraId, NormalizeToUtc(atUtc), ct);

    private async Task<ThumbnailInfo?> ResolveThumbnailInfoAsync(Guid cameraId, DateTime atUtc, CancellationToken ct)
    {
        var segment = await db.Segments
            .Where(s => s.CameraId == cameraId && s.StartUtc <= atUtc && s.EndUtc > atUtc)
            .Select(s => new { s.FilePath, s.NodeId, s.StartUtc, s.DurationMs })
            .FirstOrDefaultAsync(ct);
        if (segment is null) return null;

        var node = await db.Nodes
            .Where(n => n.Id == segment.NodeId)
            .Select(n => new { n.LastIpAddress, n.LivePort, n.MediaSigningKey })
            .FirstOrDefaultAsync(ct);

        // Clamped short of the segment's own end — an offset landing exactly on/past EndUtc would
        // ask ffmpeg to seek past the last frame this segment actually has.
        var rawOffsetSeconds = (int)(atUtc - segment.StartUtc).TotalSeconds;
        var maxOffsetSeconds = Math.Max(0, segment.DurationMs / 1000 - 1);
        var offsetSeconds = Math.Clamp(rawOffsetSeconds, 0, maxOffsetSeconds);

        return new ThumbnailInfo(segment.FilePath, offsetSeconds, node?.LastIpAddress, node?.LivePort, node?.MediaSigningKey);
    }

    /// <summary>Dashboard's "most recent thumbnail" column: the newest *completed* segment's own
    /// last frame, not GetThumbnailInfoAsync's bucketed-to-5-minutes historical lookup (which could
    /// read up to ~10 minutes stale here and, worse, land inside the still-open in-progress segment
    /// that has no Segments row yet at all, returning null). Segments only gets a row once a segment
    /// closes (RecordingSession.PollForCompletedSegments), so "newest row" already means "most recent
    /// footage actually available to extract a frame from," typically within one segment length
    /// (60s) of true "now" for an actively-recording camera. Deliberately not the live-RTSP
    /// /api/cameras/{id}/snapshot endpoint instead: that grabs a fresh frame straight from the camera
    /// every call (fine for the occasional zone-editor use it was built for, too heavy to fire once
    /// per camera on every dashboard poll) and is gated Cameras.Edit, stricter than the plain
    /// [Authorize] Pages/Index itself requires.</summary>
    public async Task<ThumbnailInfo?> GetLatestThumbnailInfoAsync(Guid cameraId, CancellationToken ct = default)
    {
        var segment = await db.Segments
            .Where(s => s.CameraId == cameraId)
            .OrderByDescending(s => s.StartUtc)
            .Select(s => new { s.FilePath, s.NodeId, s.DurationMs })
            .FirstOrDefaultAsync(ct);
        if (segment is null) return null;

        var node = await db.Nodes
            .Where(n => n.Id == segment.NodeId)
            .Select(n => new { n.LastIpAddress, n.LivePort, n.MediaSigningKey })
            .FirstOrDefaultAsync(ct);

        // As close to this segment's own end as ThumbnailCapture will accept (see
        // GetThumbnailInfoAsync's identical clamp) — the freshest frame this segment has.
        var offsetSeconds = Math.Max(0, segment.DurationMs / 1000 - 1);

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

    public async Task<List<CameraDetectionStateDto>> GetActiveDetectionsAsync(CancellationToken ct = default)
    {
        // Same recency window and same "a checkpoint every ~15s means a recent row is still
        // happening" reasoning as GetCamerasWithActiveMotionAsync — this is that query narrowed to
        // spans that carry a detected class, so a tile can say *what* it sees rather than only that
        // something moved.
        var threshold = DateTime.UtcNow - ActiveMotionStaleness;
        var rows = await db.MotionSpans
            .Where(m => m.EndUtc >= threshold && m.DetectionKind != null)
            .Select(m => new { m.CameraId, Kind = m.DetectionKind!.Value })
            .Distinct()
            .ToListAsync(ct);

        var palette = await eventColors.GetAsync(ct);

        return rows
            .GroupBy(r => r.CameraId)
            .Select(g => new CameraDetectionStateDto(
                g.Key,
                g.Select(r => r.Kind)
                    .Distinct()
                    .OrderBy(k => k)
                    .Select(k => new DetectionBadgeDto(k.ToString(), DetectionDisplay.Label(k),
                        DetectionDisplay.Emoji(k), palette.ColorFor(k)))
                    .ToList()))
            .ToList();
    }

    private const int MaxSnapshotPageSize = 100;

    /// <summary>Token this page-level filter uses for a custom-tag-sourced span — the admin-level
    /// SnapshotVisibility setting has no equivalent key, since a custom tag's own IsEnabled toggle
    /// already governs whether it exists at all; this only needs to let one viewer narrow *their own*
    /// browsing to exclude tags for the moment, not decide system-wide visibility.</summary>
    public const string CustomTagKindToken = "CustomTag";

    public async Task<SnapshotPageDto> GetSnapshotsAsync(Guid? cameraId, DateTime? fromUtc, DateTime? toUtc, int page, int pageSize, CancellationToken ct = default, IReadOnlyCollection<string>? kinds = null)
    {
        pageSize = Math.Clamp(pageSize, 1, MaxSnapshotPageSize);

        var query = db.MotionSpans.AsNoTracking().AsQueryable();
        if (cameraId is { } cid) query = query.Where(m => m.CameraId == cid);
        if (fromUtc is { } f) { f = NormalizeToUtc(f); query = query.Where(m => m.StartUtc >= f); }
        if (toUtc is { } t) { t = NormalizeToUtc(t); query = query.Where(m => m.StartUtc <= t); }

        // Admin-configurable "which event types cameras support" filter (see SnapshotVisibility) —
        // a custom EventTagRule span is never excluded here, since each rule already carries its own
        // IsEnabled toggle; only plain motion (no class, no rule) and each DetectionKind are gated.
        // settings is null in every existing test that doesn't care about this (default constructor
        // param), which resolves to "everything enabled" — the pre-existing behavior.
        if (settings is not null)
        {
            var motionEnabled = await settings.GetAsync(SnapshotVisibility.MotionKey, true, ct: ct);
            var disabledKinds = new List<DetectionKind>();
            foreach (var kind in DetectionDisplay.AllKinds)
            {
                if (!await settings.GetAsync(SnapshotVisibility.DetectionKey(kind), true, ct: ct))
                    disabledKinds.Add(kind);
            }

            query = query.Where(m =>
                m.EventTagRuleId != null
                || (m.DetectionKind != null && !disabledKinds.Contains(m.DetectionKind.Value))
                || (m.DetectionKind == null && motionEnabled));
        }

        // Page-level filter (Pages/Snapshots' own toolbar) — a pure narrowing on top of whatever the
        // admin-level filter above already allows, so a viewer can browse just "Person" for a moment
        // without touching the system-wide setting. Null/empty means no additional narrowing (every
        // checkbox ticked, or the filter never touched) — same "missing = show everything" default
        // as the admin setting.
        if (kinds is { Count: > 0 })
        {
            var kindSet = kinds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var wantMotion = kindSet.Contains("Motion");
            var wantCustomTag = kindSet.Contains(CustomTagKindToken);
            var wantedDetectionKinds = DetectionDisplay.AllKinds.Where(k => kindSet.Contains(k.ToString())).ToList();

            query = query.Where(m =>
                (m.EventTagRuleId != null && wantCustomTag)
                || (m.DetectionKind != null && wantedDetectionKinds.Contains(m.DetectionKind.Value))
                || (m.DetectionKind == null && m.EventTagRuleId == null && wantMotion));
        }

        var total = await query.CountAsync(ct);
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        var currentPage = Math.Clamp(page, 1, totalPages);

        // Zone/EventTagRule names projected via a null-conditional (left join) same as ResolveColor's
        // own ColorHex projection above — a span whose zone/rule has since been deleted still comes
        // back (ZoneId/EventTagRuleId themselves are SetNull/Restrict-nulled on delete, same as
        // TimelineBucketDto's own coloring already tolerates).
        // ThenByDescending(Id), not just StartUtc: SQL Server's OFFSET/FETCH has no guaranteed order
        // among tied rows, and several motion spans easily share the same StartUtc to the second (a
        // camera reporting multiple detection classes on the same real event, or several cameras
        // triggering in the same instant) — without a fully unique tiebreaker, the same row could
        // appear on two different pages, or a row could be skipped entirely, page to page.
        var rows = await query
            .OrderByDescending(m => m.StartUtc).ThenByDescending(m => m.Id)
            .Skip((currentPage - 1) * pageSize).Take(pageSize)
            .Select(m => new
            {
                m.Id,
                m.CameraId,
                m.StartUtc,
                m.EndUtc,
                m.ZoneId,
                m.EventTagRuleId,
                m.DetectionKind,
                ZoneName = m.Zone != null ? m.Zone.Name : null,
                RuleName = m.EventTagRule != null ? m.EventTagRule.Name : null,
                RuleColorHex = m.EventTagRule != null ? m.EventTagRule.ColorHex : null
            })
            .ToListAsync(ct);

        var cameraIds = rows.Select(r => r.CameraId).Distinct().ToList();
        var cameraNames = await db.Cameras.Where(c => cameraIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name }).ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        var palette = await eventColors.GetAsync(ct);

        // Same label/color precedence as ResolveColor above (custom tag > detected class > plain
        // motion), plus an emoji and a human label neither bucket coloring nor ResolveColor needed.
        var items = rows.Select(r =>
        {
            string label, color, emoji;
            if (r.EventTagRuleId is not null)
            {
                label = r.RuleName ?? "(deleted tag)";
                color = r.RuleColorHex ?? EventColors.DefaultMotion;
                emoji = "🏷️";
            }
            else if (r.DetectionKind is { } kind)
            {
                label = DetectionDisplay.Label(kind);
                color = palette.ColorFor(kind);
                emoji = DetectionDisplay.Emoji(kind);
            }
            else
            {
                label = r.ZoneName ?? "Motion";
                color = palette.MotionColor;
                emoji = EventColors.MotionEmoji;
            }

            // The midpoint, not the start — an event's opening instant is often the least
            // representative frame of it (a person just entering frame edge, a car mid-approach);
            // the middle is far more likely to actually show the thing that triggered the span.
            // Explicit user ask: "if an event is 20 seconds long take a snapshot from 10 seconds in".
            var duration = r.EndUtc - r.StartUtc;
            var atUtc = r.StartUtc + TimeSpan.FromTicks(duration.Ticks / 2);

            return new SnapshotDto(r.Id, r.CameraId,
                cameraNames.TryGetValue(r.CameraId, out var name) ? name : "(deleted camera)",
                atUtc, duration, label, color, emoji);
        }).ToList();

        return new SnapshotPageDto(items, totalPages, currentPage);
    }
}
