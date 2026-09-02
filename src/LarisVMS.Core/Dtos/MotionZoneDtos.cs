namespace LarisVMS.Core.Dtos;

/// <summary>Node -&gt; Web (WS /live/{cameraId}/motion-zones): one zone's current motion score, object
/// detection plan pass 3c-1's live per-zone wash on the Zones editor. Unlike the AI-detection overlay
/// (VisionLiveDetectionBox), Node needs no augmentation here — the Zones editor already has each
/// zone's own Sensitivity/Kind/enabled state loaded (it's editing them), so the Web tier proxies this
/// straight through with no per-tick parsing, and the browser does its own threshold-to-wash math.</summary>
public record MotionZoneScoreDto(Guid ZoneId, double Score);

/// <summary>Node -&gt; Web (WS /live/{cameraId}/motion-zones) wire shape, pass 3c-2 — wraps
/// MotionZoneScoreDto rather than sending a bare array (3c-1's original shape) so the same message
/// can also carry Grid mode's per-cell scores without a second socket, per the plan's own "same
/// frame, same pass, one message" call. CellScores is row-major (cell (row,col) at index
/// row*gridSize+col, matching MotionGrid's own bit layout) and null whenever the camera isn't
/// currently in Grid mode — Zones is always present (empty when there's nothing to report) so a
/// Polygon-mode camera's payload looks exactly like 3c-1 shipped, just wrapped in an object now.</summary>
public record MotionZoneOverlayPayload(List<MotionZoneScoreDto> Zones, List<double>? CellScores = null);
