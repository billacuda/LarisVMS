namespace LarisVMS.Core.Dtos;

// Wire DTOs for M7 playback/timeline (GET /api/cameras/{id}/timeline|segments, the
// /playback-segment proxy). Shared via Core the same way NodeDtos.cs is.

/// <summary>One bucket of a camera's coverage timeline. HasMotion is independent of HasRecording —
/// a bucket can have motion without recorded video (a gap in Continuous recording during a burst of
/// motion isn't possible today since Motion-mode gating hasn't landed yet, but the two flags are
/// kept orthogonal rather than motion implying recording, so that remains true whenever it does).
///
/// TagColorHex (M8 pass 8) is set when a CustomTag-sourced EventTagRule was active anywhere in this
/// bucket, taking precedence over the built-in green/blue scheme the same way motion already takes
/// precedence over plain recording — a bucket's rendered color is HasMotion ? green : HasRecording ?
/// blue : gray, UNLESS TagColorHex is set, in which case that wins outright. Null for every bucket
/// on a camera with no EventTagRules configured, so nothing about a camera's rendering changes
/// unless it opts in.
///
/// TagColorHexes carries *every* distinct colour active in the bucket, not just the winning one — a
/// camera can see a person and a vehicle in the same instant, and collapsing that to one colour hides
/// half of what happened. The renderer splits the bucket into horizontal bands, one per colour.
/// TagColorHex stays as the first of them so any consumer reading a single colour still works.</summary>
public record TimelineBucketDto(
    DateTime StartUtc,
    DateTime EndUtc,
    bool HasRecording,
    bool HasMotion,
    string? TagColorHex = null,
    IReadOnlyList<string>? TagColorHexes = null);

/// <summary>IsArchived: this segment's file has been moved to the node's archive volume (slower
/// storage). Playback still works transparently; the timeline shows a subtle marker so a longer load
/// isn't mistaken for a fault.</summary>
public record SegmentSummaryDto(long Id, DateTime StartUtc, DateTime EndUtc, bool IsArchived = false);

/// <summary>One object class currently being detected on a camera, already resolved to its display
/// form (label/emoji/color) server-side from DetectionDisplay — so the live tile's badge and the
/// timeline's coloring can't drift apart, and the client needs no copy of the class table.</summary>
public record DetectionBadgeDto(string Kind, string Label, string Emoji, string ColorHex);

/// <summary>What one camera's onboard analytics is reporting right now, for the live-view badge —
/// a camera can legitimately be seeing more than one class at once (a person next to a car).</summary>
public record CameraDetectionStateDto(Guid CameraId, IReadOnlyList<DetectionBadgeDto> Detections);

/// <summary>What the Web layer needs to proxy one segment's bytes from its owning node — the
/// node's address/port/key are null when that node has never reported live-view readiness
/// (registered before M5, or hasn't heartbeat-reported since), same condition /live already
/// checks for.</summary>
public record PlaybackSegmentInfo(string FilePath, string? NodeIp, int? NodeLivePort, string? NodeMediaSigningKey,
    /// <summary>Failover plan phase 1: the owning node's id, so the Web layer can ask
    /// MediaRoutingService whether this segment should be served by a redirect straight to the node
    /// instead of proxied. Defaulted so existing test constructions stay valid.</summary>
    Guid NodeId = default);

/// <summary>What the Web layer needs to proxy one hover-thumbnail request (M7 pass 2) — same shape
/// as PlaybackSegmentInfo plus OffsetSeconds, the offset into FilePath that
/// GetThumbnailInfoAsync already resolved server-side (the requested instant is bucketed to the
/// nearest 5 minutes before the segment lookup even happens, so this is almost always 0 — segments
/// are clock-aligned, so a 5-minute mark normally lands exactly on a segment's own start), so both
/// the signed token and the node's on-disk cache filename are built from one canonical value rather
/// than each side re-deriving it.</summary>
public record ThumbnailInfo(string FilePath, int OffsetSeconds, string? NodeIp, int? NodeLivePort, string? NodeMediaSigningKey);

/// <summary>One segment's file path plus the node it actually lives on — GetSegmentFilePathsAsync's
/// return shape for export dispatch, which (unlike playback's single-segment lookups) can span a
/// range wide enough to cross a camera's reassignment from one node to another mid-range. NodeId
/// lets ExportJobDispatcher detect that split before dispatching, rather than sending every path to
/// the camera's current node and having the node reject whatever doesn't live on its own disk.</summary>
public record SegmentFileInfo(string FilePath, Guid NodeId);

/// <summary>Object detection plan decision 5: one currently-known AI-detection category
/// (auto-registered, auto-colored — see DetectedObjectCategory's own doc comment) — what the
/// Snapshots page's filter checkboxes are built from, since this set is open-ended and can't be
/// enumerated the fixed way DetectionDisplay.AllKinds is.</summary>
public record DetectedObjectCategoryDto(Guid Id, string Name, string ColorHex);

/// <summary>One (category, specific label) pair actually observed at least once — what the
/// Snapshots page's filter tree nests under each category. Deliberately queried fresh, not a fixed
/// list: which labels exist depends entirely on which detection model produced them (today's COCO
/// vocabulary vs. a future Objects365-trained one), so this can only ever reflect what's really in
/// the database, never a hardcoded taxonomy.</summary>
public record DetectedObjectLabelDto(string CategoryName, string Label);

/// <summary>M18: one motion event rendered as a browsable "snapshot" (Pages/Snapshots) — there is no
/// separate capture step or storage for the image itself; it's whatever GetExactThumbnailInfoAsync /
/// /playback-thumbnail extracts from the actual recording at AtUtc, on demand, the same
/// frame-extraction path Playback's own hover thumbnails already use. Label/ColorHex/Emoji are
/// resolved server-side with the same precedence TimelineService's bucket coloring already applies
/// (custom EventTagRule color wins, then a detected object class's color, then plain motion), so a
/// snapshot's badge can never drift from how that same instant renders on the timeline itself.
/// CameraName is "(deleted camera)" for a camera removed since — same convention as
/// BookmarkDto/ExportDispatchCandidate.
///
/// AtUtc samples ~1s into the *recorded footage* for the underlying MotionSpan — StartUtc minus
/// Recording.MotionPreRollSeconds, plus that 1s margin — not the span's own midpoint or even StartUtc
/// itself. See GetSnapshotsAsync for the full reasoning: a span's length isn't a reliable stand-in for
/// "how long the subject was in frame" on this app's camera-pushed/detected spans (dominated by the
/// camera's own event cooldown), and the subject is typically already visible at the very start of
/// what pre-roll actually put on disk for the event, not just at StartUtc.
///
/// Duration is the underlying MotionSpan's own EndUtc-StartUtc — how long the event *span* ran, not
/// to be confused with AtUtc, which samples near its start rather than any particular fraction of
/// this duration.
///
/// IsAiDetection (object detection plan decision 10) is true for a span with a resolved
/// DetectedObjectCategory — what tells the Snapshots page to point this card's image at
/// /snapshot-image (a cropped, best-frame extraction) instead of /playback-thumbnail (a plain,
/// uncropped frame at AtUtc). GetSnapshotImageInfoAsync itself resolves gracefully to null if the
/// underlying span turns out to have no captured box after all, which the existing "No thumbnail
/// available" onerror fallback already handles — so this flag only needs to be a good default, not
/// a guarantee.
///
/// PlayFromUtc (pass 2b) is deliberately separate from AtUtc: AtUtc is the thumbnail's own sample
/// point (its AI/classified branch is intentionally StartUtc + 1s with no pre-roll — see
/// GetSnapshotsAsync's own comment for why that must never be unified with a pre-roll-based value),
/// while PlayFromUtc is pre-roll-earlier than StartUtc uniformly across every span kind, so clicking
/// into Playback actually catches the subject entering frame instead of dropping the viewer in at
/// the exact detection instant.</summary>
/// <summary>Pass G3: one badge on a snapshot card. A card usually has exactly one (its scalar
/// Label/ColorHex/Emoji, the primary), but AI-detection spans that overlap in time on the same camera
/// (a person and a dog in frame together — separate per-label spans, one cropped-from-the-same-frame
/// snapshot) collapse into one card that carries every label.</summary>
/// <summary>Confidence is the span's own BestBoxConfidence (0-1) for an AI detection, or null for a
/// camera-classified/motion/custom-tag badge — nothing outside the AI pipeline produces a score, and
/// the card renders the percentage only where there is one.</summary>
/// <summary>MovingCount is the span's peak simultaneous moving instances of this label (AI detection
/// only) — the card renders it as an "x2" / "x3" suffix when it is greater than 1, so "two people
/// walked by" reads as "Human x2" rather than the single "Human" that per-label span grouping would
/// otherwise collapse it to. Null or 1 renders no suffix.</summary>
public record SnapshotBadgeDto(string Label, string ColorHex, string Emoji, double? Confidence = null, int? MovingCount = null);

/// <summary>Id is the primary span (the one whose /snapshot-image the card requests); SpanIds is
/// every span the card represents (the primary plus any it grouped in). Badges is every label; the
/// scalar Label/ColorHex/Emoji stay as the primary so existing consumers are untouched.</summary>
public record SnapshotDto(long Id, Guid CameraId, string CameraName, DateTime AtUtc, DateTime PlayFromUtc, TimeSpan Duration, string Label, string ColorHex, string Emoji, bool IsAiDetection = false)
{
    public IReadOnlyList<SnapshotBadgeDto> Badges { get; init; } = [];
    public IReadOnlyList<long> SpanIds { get; init; } = [];
}

/// <summary>Object detection plan decision 10: what LarisVMS.Web's /snapshot-image proxy needs to
/// request one AI-detection MotionSpan's cropped best-frame image from its owning node — same shape
/// as ThumbnailInfo (FilePath/OffsetSeconds/node connection info) plus the normalized detection box
/// and the segment's own pixel dimensions, both required to compute the crop rectangle node-side.
/// FrameWidth/FrameHeight null (a segment whose resolution was never reported) means the crop can't
/// be computed at all — GetSnapshotImageInfoAsync returns null in that case rather than this record,
/// same "nothing to serve" resolution as a missing segment.
///
/// SpanId rides along so the proxy can pass it to the node as the cache-file key (see
/// StorageManager/Program.cs's own "keyed by span id, not a bucketed offset" reasoning) without a
/// second round trip to look it back up.</summary>
public record SnapshotImageInfo(string FilePath, int OffsetSeconds, long SpanId,
    double BoxX, double BoxY, double BoxW, double BoxH, int FrameWidth, int FrameHeight,
    string? NodeIp, int? NodeLivePort, string? NodeMediaSigningKey,
    /// <summary>The best-frame instant as whole milliseconds into the segment — the sub-second
    /// precision the crop's <c>-ss</c> seek needs (OffsetSeconds, kept for the signed thumbnail
    /// token's bucket identity, truncates to a whole second and can miss a fast-moving object by
    /// most of a frame). Rides unsigned, same "changes only which frame within an already-authorized
    /// ~1s window, never which file" reasoning the box coords already use.</summary>
    long OffsetMs = 0,
    /// <summary>The best-frame instant as UTC ticks — lets the node check for a pre-cropped eager
    /// snapshot (written by the high-res re-detection path, keyed by this exact instant) before
    /// falling back to an ffmpeg crop of the recorded segment. 0 when there's no best-frame time.</summary>
    long BestFrameTicksUtc = 0);

/// <summary>One page of SnapshotDto plus enough to render pagination — MotionSpans is a volume table
/// (same reasoning as Segments), too large to page client-side the way a plain sortable table does
/// elsewhere in this app, so this follows AuditLogs' own server-side-paging shape instead.</summary>
public record SnapshotPageDto(IReadOnlyList<SnapshotDto> Items, int TotalPages, int CurrentPage);

/// <summary>Body of POST /api/playback/view-opened — the audit-only ping playback-player.js fires
/// when a view is selected for review. Just the view id: the server resolves the camera set from the
/// view's own layout rather than trusting a client-supplied camera list, since this is an audit
/// record of what was actually opened.</summary>
public record ViewOpenedRequest(Guid ViewId);
