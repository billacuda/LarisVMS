namespace NidusVMS.Core.Dtos;

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
