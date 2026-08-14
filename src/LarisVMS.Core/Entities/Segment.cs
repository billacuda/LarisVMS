using LarisVMS.Core.Enums;

namespace LarisVMS.Core.Entities;

/// <summary>
/// One recorded video segment (default 60s) for one camera stream. This is the volume table the
/// plan calls out for future date-partitioning (M4) — clustered on (CameraId, StartUtc) from the
/// start so that ordering is in place before partitioning is added, even though M3 doesn't create
/// the partition function yet.
/// </summary>
public class Segment
{
    public long Id { get; set; }
    public Guid CameraId { get; set; }
    public Guid NodeId { get; set; }
    public CameraStreamRole StreamRole { get; set; }

    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public int DurationMs { get; set; }

    public string FilePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }

    public string? Codec { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public bool HasAudio { get; set; }

    /// <summary>Evidence lock (M10) — excluded from retention deletion. Defined now so the column
    /// exists before the storage manager (M4) needs it.</summary>
    public bool IsLocked { get; set; }

    public Camera Camera { get; set; } = null!;
}
