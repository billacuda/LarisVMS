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

public record SegmentSummaryDto(long Id, DateTime StartUtc, DateTime EndUtc);

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
public record PlaybackSegmentInfo(string FilePath, string? NodeIp, int? NodeLivePort, string? NodeMediaSigningKey);

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
/// this duration.</summary>
public record SnapshotDto(long Id, Guid CameraId, string CameraName, DateTime AtUtc, TimeSpan Duration, string Label, string ColorHex, string Emoji);

/// <summary>One page of SnapshotDto plus enough to render pagination — MotionSpans is a volume table
/// (same reasoning as Segments), too large to page client-side the way a plain sortable table does
/// elsewhere in this app, so this follows AuditLogs' own server-side-paging shape instead.</summary>
public record SnapshotPageDto(IReadOnlyList<SnapshotDto> Items, int TotalPages, int CurrentPage);

/// <summary>Body of POST /api/playback/view-opened — the audit-only ping playback-player.js fires
/// when a view is selected for review. Just the view id: the server resolves the camera set from the
/// view's own layout rather than trusting a client-supplied camera list, since this is an audit
/// record of what was actually opened.</summary>
public record ViewOpenedRequest(Guid ViewId);
