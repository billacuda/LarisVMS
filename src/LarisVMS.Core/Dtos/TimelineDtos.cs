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
/// unless it opts in.</summary>
public record TimelineBucketDto(DateTime StartUtc, DateTime EndUtc, bool HasRecording, bool HasMotion, string? TagColorHex = null);

public record SegmentSummaryDto(long Id, DateTime StartUtc, DateTime EndUtc);

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

/// <summary>Body of POST /api/playback/view-opened — the audit-only ping playback-player.js fires
/// when a view is selected for review. Just the view id: the server resolves the camera set from the
/// view's own layout rather than trusting a client-supplied camera list, since this is an audit
/// record of what was actually opened.</summary>
public record ViewOpenedRequest(Guid ViewId);
