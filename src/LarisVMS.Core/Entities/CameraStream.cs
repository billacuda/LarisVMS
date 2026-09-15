using LarisVMS.Core.Enums;

namespace LarisVMS.Core.Entities;

public class CameraStream
{
    public Guid Id { get; set; }
    public Guid CameraId { get; set; }

    public CameraStreamRole Role { get; set; }

    public string RtspUri { get; set; } = string.Empty;
    public string ProfileToken { get; set; } = string.Empty;

    public string? Codec { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? Fps { get; set; }
    public int? BitrateKbps { get; set; }

    /// <summary>M11: cumulative ffmpeg reconnect count for this stream since the node process
    /// started — RecordingSession.TotalReconnectCount's own doc comment explains why this is
    /// cumulative rather than the backoff-timing counter that resets on success. Reset to 0 whenever
    /// a fresh node process starts recording this stream (a new RecordingSession always starts at
    /// 0), not carried across node restarts — a lifetime-since-ever counter would need its own
    /// persistence on the node side this pass doesn't add.</summary>
    public int? ReconnectCount { get; set; }

    /// <summary>M11: when Fps/BitrateKbps/ReconnectCount were last refreshed by a heartbeat-cycle
    /// health report — lets the dashboard show "stale" data (e.g. ffmpeg died without the node itself
    /// going offline) instead of a silently frozen last-known number.</summary>
    public DateTime? HealthReportedAt { get; set; }

    /// <summary>This camera's AI-detection engine build state, from NodeWorker's own
    /// <c>_visionCameraStatus</c> (Vision Service's <c>GET /cameras/status</c>) — see
    /// <c>StreamInfoReportItem.IsEngineBuilding</c>'s own doc comment. Both null when AI detection
    /// isn't running for this camera at all; the Dashboard shows a spinner/failure badge only when
    /// one of these is true.</summary>
    public bool? IsEngineBuilding { get; set; }
    public bool? EngineBuildFailed { get; set; }

    public bool HasAudio { get; set; }

    /// <summary>Codec name and sample rate of the audio track as ffmpeg reported it when it last
    /// opened this stream (StreamAudioInfo → StreamInfoReportItem), not an ONVIF-advertised
    /// capability. Both stay null for a camera with no audio track, since nothing ever reports them.
    /// Like Width/Height/Codec these are preserved rather than cleared by later reports, so they read
    /// as "last known": a camera whose audio track is switched off keeps showing the codec it last
    /// carried, since "no audio line on this connection" arrives as no report at all rather than as
    /// an explicit clear. Re-probing the camera is what refreshes HasAudio alongside it.</summary>
    public string? AudioCodec { get; set; }
    public int? AudioSampleRateHz { get; set; }

    /// <summary>Disables this stream without touching Camera.IsEnabled — e.g. turning off a Sub
    /// stream nobody's using yet, or turning off Main to stop recording while keeping the camera and
    /// its other settings intact. NodeService.GetConfigAsync excludes disabled streams from what a
    /// node is handed, so a disabled Main stream simply stops being recorded (same "no Main stream —
    /// skipping" path NodeWorker already has for a camera with no Main stream at all).</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>User-facing override of the default "Main"/"Sub"/"Third" label — null means show the
    /// Role name as-is. Both this and IsEnabled are carried forward across re-probes (matched by
    /// ProfileToken, falling back to Role) rather than reset — see CameraService.ReplaceStreamsAsync
    /// — since a re-probe replacing the whole stream set would otherwise silently wipe them.</summary>
    public string? CustomName { get; set; }

    public Camera Camera { get; set; } = null!;
}
