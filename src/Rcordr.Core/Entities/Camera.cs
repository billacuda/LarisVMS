using Rcordr.Core.Enums;

namespace Rcordr.Core.Entities;

public class Camera
{
    public Guid Id { get; set; }

    /// <summary>Owning recorder node. Nullable until Nodes exist (M3) — a camera can be registered
    /// and probed before it has anywhere to record to.</summary>
    public Guid? NodeId { get; set; }

    public Guid? GroupId { get; set; }
    public string Name { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;
    public int OnvifPort { get; set; } = 80;

    /// <summary>The ONVIF device service address (XAddr) returned by WS-Discovery or entered
    /// manually — the entry point every other ONVIF call is made relative to.</summary>
    public string DeviceServiceUri { get; set; } = string.Empty;

    /// <summary>Encrypted at rest via SecretProtection (EncryptedNullableStringConverter).</summary>
    public string? Username { get; set; }

    /// <summary>Encrypted at rest via SecretProtection (EncryptedNullableStringConverter).</summary>
    public string? Password { get; set; }

    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? FirmwareVersion { get; set; }
    public string? SerialNumber { get; set; }

    public string? TimeZoneId { get; set; }

    /// <summary>Per-camera storage cap; null shares the pool with cameras that have no quota. See
    /// the plan's storage-manager section (M4) for eviction order.</summary>
    public long? QuotaBytes { get; set; }

    public CameraLensType LensType { get; set; } = CameraLensType.Standard;
    public string? DewarpConfigJson { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastProbedAt { get; set; }

    public CameraGroup? Group { get; set; }
    public Node? Node { get; set; }
    public CameraCapabilities? Capabilities { get; set; }
    public ICollection<CameraStream> Streams { get; set; } = [];
}
