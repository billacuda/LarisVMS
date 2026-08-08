namespace Rcordr.Core.Entities;

/// <summary>
/// The ONVIF Profile S/T/G/M support matrix for one camera, plus the individual service flags the
/// matrix is derived from. Populated by CameraCapabilityProber (Rcordr.Onvif) against
/// GetCapabilities/GetServices — this is a best-effort inference from which ONVIF services the
/// device advertises, not certified ONVIF conformance (a device can advertise a service without
/// fully implementing its profile's mandatory feature set).
/// </summary>
public class CameraCapabilities
{
    public Guid CameraId { get; set; }

    public bool ProfileS { get; set; }
    public bool ProfileT { get; set; }
    public bool ProfileG { get; set; }
    public bool ProfileM { get; set; }

    public bool HasPtz { get; set; }
    public bool HasAudioOut { get; set; }
    public bool HasRelayOutputs { get; set; }
    public bool HasDigitalInputs { get; set; }
    public bool HasAnalyticsMetadata { get; set; }
    public bool HasImaging { get; set; }
    public bool HasEvents { get; set; }
    public bool HasMedia2 { get; set; }

    /// <summary>Raw GetCapabilities/GetServices XAddr map, kept for troubleshooting vendor quirks
    /// without needing to re-probe the device.</summary>
    public string? RawProbeJson { get; set; }

    public DateTime ProbedAt { get; set; }

    public Camera Camera { get; set; } = null!;
}
