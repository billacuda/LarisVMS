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
                m.DetectionKind,
                // Object detection plan decision 5: same null-conditional-navigation left join as
                // ColorHex above — a span whose category still exists projects its color; the
                // category is never expected to be deleted (see DetectedObjectCategory's own doc
                // comment), but the same tolerance costs nothing to keep.
                AiCategoryColorHex = m.DetectedObjectCategory != null ? m.DetectedObjectCategory.ColorHex : null,
                AiCategoryName = m.DetectedObjectCategory != null ? m.DetectedObjectCategory.Name : null
            })
            .ToListAsync(ct);

        var palette = await eventColors.GetAsync(ct);

        return Bucket(segments.Select(s => (s.StartUtc, s.EndUtc)),
            motionSpans.Select(m => (m.StartUtc, m.EndUtc, ResolveColor(palette, m.ColorHex, m.DetectionKind, m.AiCategoryName, m.AiCategoryColorHex))),
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
                m.DetectionKind,
                AiCategoryColorHex = m.DetectedObjectCategory != null ? m.DetectedObjectCategory.ColorHex : null,
                AiCategoryName = m.DetectedObjectCategory != null ? m.DetectedObjectCategory.Name : null
            })
            .ToListAsync(ct);

        var palette = await eventColors.GetAsync(ct);

        return Bucket(segments.Select(s => (s.StartUtc, s.EndUtc)),
            motionSpans.Select(m => (m.StartUtc, m.EndUtc, ResolveColor(palette, m.ColorHex, m.DetectionKind, m.AiCategoryName, m.AiCategoryColorHex))),
            fromUtc, toUtc, bucketCount);
    }

    /// <summary>A span's timeline color. A user-configured EventTagRule's own color still wins
    /// outright (it's an explicit choice, unlike an inferred object class); a camera-native object
    /// detection (DetectionKind) supplies its class color next — whatever an admin picked on
    /// Admin/Event Colors, or the built-in default for that class; an AI-detection span
    /// (object detection plan decisions 5/9 — DetectionKind stays null for these, see MotionSpan's
    /// own doc comment) resolves through that same Events palette next, by mapping its
    /// DetectedObjectCategory's name onto the DetectionKind it represents (see
    /// EventPalette.ColorForAiCategory) — so an admin-chosen Human/Vehicle/Animal/Object colour
    /// governs both a camera-classified span and an AI-detected one, rather than the AI side
    /// following its own internal auto-assigned colour no settings page ever showed; anything else
    /// falls through to the plain motion/recording scheme by returning null. Reusing the existing
    /// TagColorHex channel rather than adding a second color field means the canvas renderer needs
    /// no new concept.</summary>
    private static string? ResolveColor(EventPalette palette, string? tagColorHex, DetectionKind? detectionKind,
        string? aiCategoryName = null, string? aiCategoryColorHex = null)
        => tagColorHex
           ?? (detectionKind is { } kind ? palette.ColorFor(kind) : null)
           // Was the category row's own stored ColorHex directly, which is never shown in the Events
           // settings editor — so an AI-detected human ignored the Human colour an admin had set there
           // and drew in whatever DetectedObjectColorAssigner happened to pick. See
           // EventPalette.ColorForAiCategory.
           ?? (aiCategoryName is not null ? palette.ColorForAiCategory(aiCategoryName, aiCategoryColorHex) : null);

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

    public async Task<SnapshotImageInfo?> GetSnapshotImageInfoAsync(Guid cameraId, long spanId, CancellationToken ct = default)
    {
        var span = await db.MotionSpans
            .Where(m => m.Id == spanId && m.CameraId == cameraId)
            .Select(m => new { m.StartUtc, m.BestFrameAtUtc, m.BestBoxX, m.BestBoxY, m.BestBoxW, m.BestBoxH })
            .FirstOrDefaultAsync(ct);
        // No captured box means either this isn't an AI-detection span, or a checkpoint never
        // reached the vision pipeline's best-frame bookkeeping — either way, nothing to crop.
        if (span is null || span.BestBoxX is not { } boxX || span.BestBoxY is not { } boxY ||
            span.BestBoxW is not { } boxW || span.BestBoxH is not { } boxH)
            return null;

        var atUtc = NormalizeToUtc(span.BestFrameAtUtc ?? span.StartUtc);
        var segment = await db.Segments
            .Where(s => s.CameraId == cameraId && s.StartUtc <= atUtc && s.EndUtc > atUtc)
            .Select(s => new { s.FilePath, s.NodeId, s.StartUtc, s.DurationMs, s.Width, s.Height })
            .FirstOrDefaultAsync(ct);
        // A segment with no reported resolution can't have its crop rectangle computed at all —
        // same "nothing to serve" resolution as no covering segment existing in the first place.
        if (segment is null || segment.Width is not { } frameWidth || segment.Height is not { } frameHeight)
            return null;

        var node = await db.Nodes
            .Where(n => n.Id == segment.NodeId)
            .Select(n => new { n.LastIpAddress, n.LivePort, n.MediaSigningKey })
            .FirstOrDefaultAsync(ct);

        // Same clamp reasoning as ResolveThumbnailInfoAsync above — but keep whole-millisecond
        // precision for the crop's own seek (the integer-second value is only kept for the signed
        // thumbnail token's bucket identity; truncating the seek to a whole second can miss a
        // fast-moving object by most of a frame, one of the two causes of the Snapshots page not
        // lining up with what was detected).
        var rawOffsetMs = (long)(atUtc - segment.StartUtc).TotalMilliseconds;
        var maxOffsetMs = Math.Max(0, (long)segment.DurationMs - 1);
        var offsetMs = Math.Clamp(rawOffsetMs, 0, maxOffsetMs);
        var offsetSeconds = (int)(offsetMs / 1000);

        var bestFrameTicks = span.BestFrameAtUtc is { } bf ? NormalizeToUtc(bf).Ticks : 0;

        return new SnapshotImageInfo(segment.FilePath, offsetSeconds, spanId, boxX, boxY, boxW, boxH,
            frameWidth, frameHeight, node?.LastIpAddress, node?.LivePort, node?.MediaSigningKey,
            offsetMs, bestFrameTicks);
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

    /// <summary>How far past the start of the *recorded footage* (not the span) to sample a
    /// Snapshots thumbnail — see GetSnapshotsAsync's own comment.</summary>
    internal static readonly TimeSpan SnapshotOffsetIntoRecording = TimeSpan.FromSeconds(1);

    public async Task<SnapshotPageDto> GetSnapshotsAsync(IReadOnlyCollection<Guid>? cameraIds, DateTime? fromUtc, DateTime? toUtc, int page, int pageSize, CancellationToken ct = default, IReadOnlyCollection<string>? kinds = null, IReadOnlyCollection<string>? excludedLabels = null)
    {
        pageSize = Math.Clamp(pageSize, 1, MaxSnapshotPageSize);

        var query = db.MotionSpans.AsNoTracking().AsQueryable();
        if (cameraIds is not null) query = query.Where(m => cameraIds.Contains(m.CameraId));
        if (fromUtc is { } f) { f = NormalizeToUtc(f); query = query.Where(m => m.StartUtc >= f); }
        if (toUtc is { } t) { t = NormalizeToUtc(t); query = query.Where(m => m.StartUtc <= t); }

        // Pass 2c's footage-coverage guard is deliberately NOT part of this query — see the
        // "does footage still cover this span" block further down, after pagination, for why.

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
        // admin-level filter above already allows, so a viewer can browse just "Human" for a moment
        // without touching the system-wide setting. Null/empty means no additional narrowing (every
        // checkbox ticked, or the filter never touched) — same "missing = show everything" default
        // as the admin setting.
        if (kinds is { Count: > 0 })
        {
            var kindSet = kinds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var wantMotion = kindSet.Contains("Motion");
            var wantCustomTag = kindSet.Contains(CustomTagKindToken);
            var wantedDetectionKinds = DetectionDisplay.AllKinds.Where(k => kindSet.Contains(k.ToString())).ToList();
            // Object detection plan decision 5: DetectedObjectCategory names are also valid filter
            // tokens, alongside "Motion"/"CustomTag"/a DetectionKind name. Deliberately NOT
            // mutually exclusive with wantedDetectionKinds above — "Vehicle" and "Animal" are both a
            // DetectionKind value *and* one of the small fixed AI category names by design, and a
            // single "Vehicle" checkbox should match either source's spans, not just whichever one
            // claimed the token first. Only the two reserved system tokens are excluded.
            var wantedCategoryNames = kindSet
                .Where(k => !k.Equals("Motion", StringComparison.OrdinalIgnoreCase)
                    && !k.Equals(CustomTagKindToken, StringComparison.OrdinalIgnoreCase))
                .ToList();

            query = query.Where(m =>
                (m.EventTagRuleId != null && wantCustomTag)
                || (m.DetectionKind != null && wantedDetectionKinds.Contains(m.DetectionKind.Value))
                || (m.DetectedObjectCategoryId != null && m.DetectedObjectCategory != null && wantedCategoryNames.Contains(m.DetectedObjectCategory.Name))
                || (m.DetectionKind == null && m.EventTagRuleId == null && m.DetectedObjectCategoryId == null && wantMotion));
        }

        // Filter-tree narrowing, one level below kinds: excludes a specific "{category}:{label}"
        // pair (e.g. "Vehicle:truck") while its category and sibling labels stay included — a
        // camera-native DetectionKind span has no specific label at all, so it's never touched here.
        if (excludedLabels is { Count: > 0 })
        {
            var excludedSet = excludedLabels.ToHashSet(StringComparer.OrdinalIgnoreCase);
            query = query.Where(m =>
                m.DetectedObjectCategory == null || m.DetectedObjectLabel == null
                || !excludedSet.Contains(m.DetectedObjectCategory.Name + ":" + m.DetectedObjectLabel));
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
                RuleColorHex = m.EventTagRule != null ? m.EventTagRule.ColorHex : null,
                // Object detection plan decisions 5/9: an AI-detection span (DetectionKind null,
                // DetectedObjectCategoryId set) carries its category's own stored color plus the
                // specific label riding alongside it (DetectedObjectLabel) — see the item-building
                // loop below for how the two combine into one badge.
                m.DetectedObjectLabel,
                m.BestBoxConfidence,
                AiCategoryName = m.DetectedObjectCategory != null ? m.DetectedObjectCategory.Name : null,
                AiCategoryColorHex = m.DetectedObjectCategory != null ? m.DetectedObjectCategory.ColorHex : null
            })
            .ToListAsync(ct);

        var pageCameraIds = rows.Select(r => r.CameraId).Distinct().ToList();
        var cameraNames = await db.Cameras.Where(c => pageCameraIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name }).ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        // Recording.MotionPreRollSeconds per camera — resolved once per distinct camera on this page
        // (ISettingsResolver caches, so this is cheap even across many rows) rather than per row.
        // Camera-level override only, no nodeId: MotionSpan doesn't carry which node scored it, and a
        // node-level pre-roll override is a rarer case than a camera-level one — see
        // GetSnapshotsAsync's own comment for why this value matters here at all. settings is null in
        // every existing test that doesn't care about this (default constructor param), which
        // resolves to 10 — NodeService's own hardcoded default for the same setting.
        var preRollSecondsByCameraId = new Dictionary<Guid, int>();
        foreach (var camId in pageCameraIds)
        {
            preRollSecondsByCameraId[camId] = settings is not null
                ? await settings.GetAsync("Recording.MotionPreRollSeconds", 10, cameraId: camId, ct: ct)
                : 10;
        }

        // Pass 2c: a card that can't be played back is noise — hide a span with no footage actually
        // covering it, so a "No thumbnail available" placeholder never appears in the first place
        // (MotionSpanRetentionService's 6-hour sweep then reclaims the row itself; this is what makes
        // the page correct immediately, not just eventually).
        //
        // Checked HERE, per rendered row, rather than as a predicate on the paged query above — three
        // successive attempts to express it as one query all failed live, and the reason is
        // structural, not a matter of finding better LINQ:
        //   * `Any()` combined with `Min()` in one Where predicate, and a `Join` against a `GroupBy`
        //     with DateTime-minus-TimeSpan arithmetic, both threw "could not be translated".
        //   * A correlated `Segments.Any(overlap test)` translated fine but timed out, even after
        //     adding a (CameraId, EndUtc) index. Coverage is an *interval-overlap* test
        //     (segment.StartUtc < span.EndUtc AND segment.EndUtc > span's play-from point), and a
        //     B-tree can only seek on one range boundary — the other side is always a residual scan.
        //     As a correlated subquery that runs per candidate row, and CountAsync makes "candidate"
        //     mean every MotionSpan the filters allow (tens of thousands) against a Segments table
        //     with a segment per minute per camera. No index makes that shape fast.
        // Against at most one page of already-materialized rows, the same test is bounded and every
        // predicate is a constant, so each check is an ordinary index seek. It also gets to use each
        // camera's own resolved pre-roll rather than a single global value.
        //
        // Trade-off, deliberate: `total`/`totalPages` above are computed WITHOUT this filter, so a
        // page can render fewer than pageSize cards and the count can overstate slightly. Making the
        // count exact would require the coverage test back inside the query — the exact thing that
        // cannot be made fast. An approximate count is worth a page that loads.
        var coveredSpanIds = new HashSet<long>();
        // Cross-source dedup (B3): a camera's own analytics reports an object as a DetectionKind span
        // (no label, no crop); LarisVMS AI detection reports the same object as a DetectedObjectCategory
        // span (with a real best-frame crop). When an overlapping AI span of a compatible category
        // exists on the same camera, hide the camera-native one — the AI card is strictly better.
        // Checked HERE, per already-materialized row, for exactly the reason the coverage test above
        // is: a correlated subquery on MotionSpans inside the paged query (and, worse, inside
        // CountAsync) is the one shape that cannot be made fast on this table. Same deliberate
        // trade-off — total/totalPages can overstate by a few; the timeline still shows both spans.
        var suppressedSpanIds = new HashSet<long>();
        foreach (var r in rows)
        {
            var cameraId = r.CameraId;
            var spanEndUtc = r.EndUtc;
            var spanStartUtc = r.StartUtc;
            var playFromUtc = r.StartUtc.AddSeconds(-preRollSecondsByCameraId.GetValueOrDefault(r.CameraId, 10));
            if (await db.Segments.AnyAsync(s => s.CameraId == cameraId
                && s.StartUtc < spanEndUtc && s.EndUtc > playFromUtc, ct))
            {
                coveredSpanIds.Add(r.Id);
            }

            var supersedingCategory = r.DetectionKind switch
            {
                DetectionKind.Human or DetectionKind.Face => "Human",
                DetectionKind.Vehicle => "Vehicle",
                DetectionKind.Animal => "Animal",
                _ => null
            };
            if (supersedingCategory is not null && await db.MotionSpans.AnyAsync(ai =>
                    ai.CameraId == cameraId && ai.DetectedObjectCategoryId != null
                    && ai.DetectedObjectCategory!.Name == supersedingCategory
                    && ai.StartUtc <= spanEndUtc && ai.EndUtc >= spanStartUtc, ct))
            {
                suppressedSpanIds.Add(r.Id);
            }
        }
        rows = rows.Where(r => coveredSpanIds.Contains(r.Id) && !suppressedSpanIds.Contains(r.Id)).ToList();

        var palette = await eventColors.GetAsync(ct);

        // Pass G3: collapse AI-detection spans that overlap in time on the same camera into one card
        // — a person and a dog crossing frame together are separate per-label spans (the per-label
        // grain is deliberate everywhere upstream) but, via the Vision Service's same-frame crop, one
        // snapshot. Grouped within this already-materialized page only; a rare cross-page split just
        // shows two cards, exactly the pre-G3 behavior. Non-AI spans never group.
        var groupPrimaryByRowId = new Dictionary<long, long>();
        var groupMemberIdsByPrimary = new Dictionary<long, List<long>>();
        {
            var aiRows = rows.Where(r => r.AiCategoryName is not null).ToList();
            var assigned = new HashSet<long>();
            foreach (var seed in aiRows)
            {
                if (!assigned.Add(seed.Id)) continue;
                var members = new List<long> { seed.Id };
                var winStart = seed.StartUtc;
                var winEnd = seed.EndUtc;
                bool grew = true;
                while (grew)
                {
                    grew = false;
                    foreach (var cand in aiRows)
                    {
                        if (assigned.Contains(cand.Id) || cand.CameraId != seed.CameraId) continue;
                        // Actual interval overlap (1s edge tolerance) — a person then a car 25s later
                        // must stay two cards; only genuinely-together objects merge.
                        if (cand.StartUtc <= winEnd.AddSeconds(1) && cand.EndUtc >= winStart.AddSeconds(-1))
                        {
                            members.Add(cand.Id);
                            assigned.Add(cand.Id);
                            if (cand.StartUtc < winStart) winStart = cand.StartUtc;
                            if (cand.EndUtc > winEnd) winEnd = cand.EndUtc;
                            grew = true;
                        }
                    }
                }

                // Primary = highest best-box confidence (best crop), tie-broken to the most recent
                // start (already the DESC order). Its id is what the card's /snapshot-image requests
                // — and since G1 gives grouped spans a shared BestFrameAtUtc, any member's id resolves
                // to the same union crop anyway.
                var primary = members
                    .OrderByDescending(id => aiRows.First(r => r.Id == id).BestBoxConfidence ?? -1)
                    .ThenByDescending(id => aiRows.First(r => r.Id == id).StartUtc)
                    .First();
                foreach (var id in members) groupPrimaryByRowId[id] = primary;
                groupMemberIdsByPrimary[primary] = members;
            }
        }

        (string Label, string Color, string Emoji, double? Confidence) AiBadge(long rowId)
        {
            var row = rows.First(x => x.Id == rowId);
            var lbl = row.DetectedObjectLabel is { } sl ? $"{row.AiCategoryName} — {sl}" : row.AiCategoryName!;
            return (lbl, palette.ColorForAiCategory(row.AiCategoryName, row.AiCategoryColorHex),
                CocoCategoryMap.Emoji(row.AiCategoryName!), row.BestBoxConfidence);
        }

        // Same label/color precedence as ResolveColor above (custom tag > detected class > plain
        // motion), plus an emoji and a human label neither bucket coloring nor ResolveColor needed.
        var items = rows.Select(r =>
        {
            // G3: a non-primary member of an AI group renders no card of its own.
            if (r.AiCategoryName is not null && groupPrimaryByRowId.TryGetValue(r.Id, out var gp) && gp != r.Id)
                return (SnapshotDto?)null;

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
            else if (r.AiCategoryName is not null)
            {
                // Object detection plan decision 5: combined "category — label" text, not just the
                // category — a card that only said "Vehicle" would lose exactly the detail
                // (car vs. truck vs. bus) the open-ended category system exists to preserve.
                label = r.DetectedObjectLabel is { } specificLabel
                    ? $"{r.AiCategoryName} — {specificLabel}"
                    : r.AiCategoryName;
                // The Events settings palette, not the category row's own auto-assigned ColorHex —
                // see EventPalette.ColorForAiCategory for why that divergence was a bug.
                color = palette.ColorForAiCategory(r.AiCategoryName, r.AiCategoryColorHex);
                emoji = CocoCategoryMap.Emoji(r.AiCategoryName);
            }
            else
            {
                label = r.ZoneName ?? "Motion";
                color = palette.MotionColor;
                emoji = EventColors.MotionEmoji;
            }

            var duration = r.EndUtc - r.StartUtc;
            // Sample point depends on whether this span has a classified object behind it.
            //
            // A classified detection (Person/Vehicle/Face/Animal/Object) fires the instant the camera's
            // own classifier confirms what it saw — the subject is already on-frame right at StartUtc,
            // and the span's own length past that is dominated by the camera's event cooldown/anti-dither
            // (~10s or more on some hardware, so it doesn't spam), not by how long the subject stuck
            // around. StartUtc + a one-second margin is what actually shows the subject; going back
            // further into the pre-roll buffer risks landing *before* the subject entered frame — an
            // empty-scene thumbnail for exactly the event that most needs a good one. Confirmed live as
            // a real regression once the pre-roll-based sample point (below) was applied here too: wrong
            // thumbnails, plus a spike of 502s from /playback-thumbnail's exact lookup landing in the
            // wrong segment or right at a segment's drift-prone tail.
            //
            // Plain motion and a custom event-tag rule (no classified kind) get no such "already
            // visible" guarantee — generic movement can still be arriving as the trigger fires, so the
            // *recording's* own pre-roll (which starts Recording.MotionPreRollSeconds before StartUtc)
            // is more likely to actually catch the subject entering frame than StartUtc itself is.
            //
            // AI detection gets the same "already visible at StartUtc" treatment as a classified
            // DetectionKind — ByteTrack's own confirmation logic already gates span start the same way
            // a camera's onboard classifier does, so the subject is on-frame right away here too.
            //
            // No lower clamp on the pre-roll candidate: landing before any segment this camera actually
            // has on disk (an event moments after recording began, with less than a full pre-roll
            // buffer built up yet) resolves to no thumbnail, same "No thumbnail available" placeholder
            // any other missing-footage case already shows.
            DateTime atUtc;
            if (r.DetectionKind is not null || r.AiCategoryName is not null)
            {
                var candidate = r.StartUtc + SnapshotOffsetIntoRecording;
                atUtc = candidate > r.EndUtc ? r.EndUtc : candidate;
            }
            else
            {
                var preRollSeconds = preRollSecondsByCameraId.GetValueOrDefault(r.CameraId, 10);
                var candidate = r.StartUtc - TimeSpan.FromSeconds(preRollSeconds) + SnapshotOffsetIntoRecording;
                atUtc = candidate > r.EndUtc ? r.EndUtc : candidate;
            }

            // PlayFromUtc (pass 2b): where Playback should actually start, as opposed to atUtc's
            // thumbnail sample point above. One rule for every span kind — start at the beginning of
            // whatever pre-roll actually put on disk — rather than atUtc's two-branch split, since
            // there's no "already visible" regression risk here the way there was for the thumbnail
            // (see SnapshotDto's own doc comment). No upper clamp needed: StartUtc minus a pre-roll is
            // always <= StartUtc <= EndUtc. No lower clamp either, for the same reason atUtc's own
            // branch above tolerates one: an event moments after recording began has no full pre-roll
            // buffer yet, and Playback already handles a deep-link instant with nothing behind it the
            // same way scrubbing into any gap does.
            var playFromPreRollSeconds = preRollSecondsByCameraId.GetValueOrDefault(r.CameraId, 10);
            var playFromUtc = r.StartUtc - TimeSpan.FromSeconds(playFromPreRollSeconds);

            // G3: a grouped AI primary carries every member's badge; everything else is a single
            // badge matching its own scalar fields. SpanIds lets the card key any group member.
            IReadOnlyList<SnapshotBadgeDto> badges;
            IReadOnlyList<long> spanIds;
            if (r.AiCategoryName is not null && groupMemberIdsByPrimary.TryGetValue(r.Id, out var memberIds) && memberIds.Count > 1)
            {
                badges = memberIds
                    .Select(AiBadge)
                    // Highest-scoring first within a label, so the one kept by DistinctBy carries the
                    // confidence actually worth showing — the same "best box wins" rule the group's
                    // own primary/crop selection already uses above.
                    .OrderByDescending(b => b.Confidence ?? -1)
                    .DistinctBy(b => b.Label)
                    .Select(b => new SnapshotBadgeDto(b.Label, b.Color, b.Emoji, b.Confidence))
                    .ToList();
                spanIds = memberIds;
            }
            else
            {
                // Only an AI span has a score; a camera-classified, motion or custom-tag badge leaves
                // it null and renders no percentage.
                badges = [new SnapshotBadgeDto(label, color, emoji,
                    r.AiCategoryName is not null ? r.BestBoxConfidence : null)];
                spanIds = [r.Id];
            }

            return (SnapshotDto?)new SnapshotDto(r.Id, r.CameraId,
                cameraNames.TryGetValue(r.CameraId, out var name) ? name : "(deleted camera)",
                atUtc, playFromUtc, duration, label, color, emoji, IsAiDetection: r.AiCategoryName is not null)
            {
                Badges = badges,
                SpanIds = spanIds,
            };
        }).Where(x => x is not null).Select(x => x!).ToList();

        return new SnapshotPageDto(items, totalPages, currentPage);
    }

    public async Task<List<DetectedObjectCategoryDto>> GetDetectedObjectCategoriesAsync(CancellationToken ct = default)
    {
        return await db.DetectedObjectCategories.AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new DetectedObjectCategoryDto(c.Id, c.Name, c.ColorHex))
            .ToListAsync(ct);
    }

    public async Task<List<DetectedObjectLabelDto>> GetDetectedObjectLabelsAsync(CancellationToken ct = default)
    {
        return await db.MotionSpans.AsNoTracking()
            .Where(m => m.DetectedObjectCategory != null && m.DetectedObjectLabel != null)
            .Select(m => new { CategoryName = m.DetectedObjectCategory!.Name, Label = m.DetectedObjectLabel! })
            .Distinct()
            .OrderBy(x => x.CategoryName).ThenBy(x => x.Label)
            .Select(x => new DetectedObjectLabelDto(x.CategoryName, x.Label))
            .ToListAsync(ct);
    }
}
