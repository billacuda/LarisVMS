namespace LarisVMS.Core.Interfaces;

/// <summary>
/// M18 "basic PTZ" — continuous pan/tilt/zoom against a camera's own ONVIF PTZ service, called
/// directly from LarisVMS.Web the same way camera probing already does (the "onvif" named
/// HttpClient — see Program.cs), not routed through a recorder node: a PTZ command is a quick
/// request/response, not a long-lived session, so there's no reason to add a node hop it doesn't
/// need. MoveAsync/StopAsync both return false (rather than throwing) when the camera has no usable
/// PTZ target — no CameraCapabilities.HasPtz, no resolvable PTZ XAddr, or no Main stream profile
/// token — so a caller can tell "camera doesn't support this" apart from "camera is unreachable"
/// (which surfaces as a thrown OnvifFaultException/HttpRequestException instead).
/// </summary>
public interface IPtzService
{
    /// <summary>panX/tiltY/zoomX are each clamped to -1..1 (0 = don't move that axis) before being
    /// sent as an ONVIF ContinuousMove velocity.</summary>
    Task<bool> MoveAsync(Guid cameraId, double panX, double tiltY, double zoomX, CancellationToken ct = default);

    Task<bool> StopAsync(Guid cameraId, CancellationToken ct = default);
}
