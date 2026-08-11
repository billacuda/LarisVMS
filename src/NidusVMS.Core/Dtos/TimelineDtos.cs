namespace NidusVMS.Core.Dtos;

// Wire DTOs for M7 playback/timeline (GET /api/cameras/{id}/timeline|segments, the
// /playback-segment proxy). Shared via Core the same way NodeDtos.cs is.

/// <summary>One bucket of a camera's coverage timeline. HasMotion is independent of HasRecording —
/// a bucket can have motion without recorded video (a gap in Continuous recording during a burst of
/// motion isn't possible today since Motion-mode gating hasn't landed yet, but the two flags are
/// kept orthogonal rather than motion implying recording, so that remains true whenever it does).</summary>
public record TimelineBucketDto(DateTime StartUtc, DateTime EndUtc, bool HasRecording, bool HasMotion);

public record SegmentSummaryDto(long Id, DateTime StartUtc, DateTime EndUtc);

/// <summary>What the Web layer needs to proxy one segment's bytes from its owning node — the
/// node's address/port/key are null when that node has never reported live-view readiness
/// (registered before M5, or hasn't heartbeat-reported since), same condition /live already
/// checks for.</summary>
public record PlaybackSegmentInfo(string FilePath, string? NodeIp, int? NodeLivePort, string? NodeMediaSigningKey);
