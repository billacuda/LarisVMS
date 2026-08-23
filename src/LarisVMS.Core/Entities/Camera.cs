using LarisVMS.Core.Enums;

namespace LarisVMS.Core.Entities;

public class Camera
{
    public Guid Id { get; set; }

    /// <summary>Owning recorder node. Nullable until Nodes exist (M3) — a camera can be registered
    /// and probed before it has anywhere to record to.</summary>
    public Guid? NodeId { get; set; }

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

    /// <summary>Which vendor integration this camera needs beyond plain ONVIF, as
    /// ICameraIntegrationProvider.Key — set automatically from the make/model reported during
    /// probing (see CameraIntegrations.Detect), null for the majority of cameras that need nothing.
    /// Stored rather than re-derived on demand so a node's config generation doesn't depend on
    /// re-running detection, and so an unrecognized key from a newer/older version degrades to "no
    /// integration" instead of failing.</summary>
    public string? IntegrationKey { get; set; }

    /// <summary>Which physical sensor of a multi-sensor device (e.g. an Axis quad-lens) this camera
    /// row represents — the ONVIF VideoSourceToken its profiles draw from. Null for the ordinary
    /// single-sensor case, and for every camera added before multi-channel support existed: null
    /// means "use every profile this device reports", exactly the pre-existing behavior. When set,
    /// probing considers only the profiles belonging to this sensor, so several Camera rows can share
    /// one device (same Host/DeviceServiceUri/credentials) while each records its own lens
    /// independently — which is what lets the whole recording/View/Live/Playback pipeline treat a
    /// multi-sensor device as N ordinary cameras with no changes of its own.</summary>
    public string? VideoSourceToken { get; set; }

    public string? TimeZoneId { get; set; }

    /// <summary>Per-camera storage cap; null shares the pool with cameras that have no quota. See
    /// the plan's storage-manager section (M4) for eviction order.</summary>
    public long? QuotaBytes { get; set; }

    public CameraLensType LensType { get; set; } = CameraLensType.Standard;
    public string? DewarpConfigJson { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastProbedAt { get; set; }

    /// <summary>Every camera group this camera belongs to — a camera can be in any number of groups,
    /// but (enforced at the application layer, not the schema, by CameraGroupPolicy) all of them must
    /// share the same top-level ancestor: a camera belongs to exactly one Site, and any number of
    /// groups/sub-groups beneath it. Many-to-many (CameraGroupMemberships join table, EF-managed, no
    /// explicit entity class needed since it carries no columns of its own).</summary>
    public ICollection<CameraGroup> Groups { get; set; } = [];

    public Node? Node { get; set; }
    public CameraCapabilities? Capabilities { get; set; }
    public ICollection<CameraStream> Streams { get; set; } = [];
}
