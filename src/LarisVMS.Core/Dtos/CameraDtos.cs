namespace LarisVMS.Core.Dtos;

/// <summary>One WS-Discovery result, before it becomes a Camera row. Mirrors
/// LarisVMS.Onvif.Discovery.DiscoveredDevice but lives in Core so Web doesn't need a reference to
/// LarisVMS.Onvif just to render the discovery page.</summary>
public record DiscoveredCameraDto(string EndpointReference, IReadOnlyList<string> XAddrs,
    string? HardwareHint, string? NameHint, string RemoteAddress);

public record AddCameraRequest(
    string Name, string DeviceServiceUri, string? Username, string? Password, Guid? GroupId);

/// <summary>VideoSourceTokens: every distinct physical sensor the probed device exposes (see
/// OnvifMediaProfile.VideoSourceToken). Empty or one entry is the ordinary single-lens camera; more
/// than one means this is a multi-sensor device (e.g. an Axis quad-lens) whose lenses can each be
/// split into their own Camera row via ICameraService.SplitChannelsAsync.</summary>
public record CameraProbeSummary(
    bool ProfileS, bool ProfileT, bool ProfileG, bool ProfileM,
    bool HasPtz, bool HasImaging, bool HasEvents, bool HasAnalyticsMetadata,
    bool HasMedia2, bool HasAudioOut, bool HasRelayOutputs, bool HasDigitalInputs,
    int StreamCount, string? Error, IReadOnlyList<string>? VideoSourceTokens = null);

/// <summary>M18 "basic PTZ" — a continuous-move request from the Live page's directional pad.
/// PanX/TiltY/ZoomX are each clamped to -1..1 server-side (IPtzService) before being sent on to the
/// camera; 0 on an axis means "don't move it".</summary>
public record PtzMoveRequest(double PanX, double TiltY, double ZoomX);

/// <summary>Per (camera, stale node) detail behind ICameraService.GetStaleSegmentNodeIdsAsync's bare
/// counts — the camera's own name and the newest EndUtc still stuck on that node, which is what
/// Admin/Nodes' warning tooltip needs to say "camera X — falls off &lt;date&gt;" instead of just a
/// count. NewestSegmentEndUtc, not the oldest: the node's own retention sweep
/// (StorageManager.SelectRetentionEvictions) evicts file-by-file once each one's age exceeds the
/// retention window, so the *last* of a camera's stale segments on this node — not the first — is
/// what determines when this camera stops appearing in the warning at all.</summary>
public record StaleSegmentDetail(Guid CameraId, string CameraName, Guid NodeId, DateTime NewestSegmentEndUtc);
