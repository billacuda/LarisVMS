namespace NidusVMS.Core.Dtos;

// Wire DTOs for M7 playback/timeline (GET /api/cameras/{id}/timeline|segments, the
// /playback-segment proxy). Shared via Core the same way NodeDtos.cs is.

/// <summary>One bucket of a camera's coverage timeline. No motion field yet — that's M8
/// (MotionSpans doesn't exist until then); the client renders every bucket as recorded/gap only
/// for this pass, not recorded/motion/gap.</summary>
public record TimelineBucketDto(DateTime StartUtc, DateTime EndUtc, bool HasRecording);

public record SegmentSummaryDto(long Id, DateTime StartUtc, DateTime EndUtc);

/// <summary>What the Web layer needs to proxy one segment's bytes from its owning node — the
/// node's address/port/key are null when that node has never reported live-view readiness
/// (registered before M5, or hasn't heartbeat-reported since), same condition /live already
/// checks for.</summary>
public record PlaybackSegmentInfo(string FilePath, string? NodeIp, int? NodeLivePort, string? NodeMediaSigningKey);
