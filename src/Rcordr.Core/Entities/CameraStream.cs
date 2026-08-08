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

    public Camera Camera { get; set; } = null!;
}
