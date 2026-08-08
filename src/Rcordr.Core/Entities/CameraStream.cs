using Rcordr.Core.Enums;

namespace Rcordr.Core.Entities;

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

    public bool HasAudio { get; set; }
    public string? AudioCodec { get; set; }

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
