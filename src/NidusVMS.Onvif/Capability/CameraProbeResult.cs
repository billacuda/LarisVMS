using NidusVMS.Onvif.Clients;

namespace NidusVMS.Onvif.Capability;

public record CameraProbeResult(
    OnvifDeviceInformation DeviceInfo,
    bool ProfileS, bool ProfileT, bool ProfileG, bool ProfileM,
    bool HasPtz, bool HasImaging, bool HasEvents, bool HasAnalyticsMetadata,
    bool HasMedia2, bool HasAudioOut, bool HasRelayOutputs, bool HasDigitalInputs,
    IReadOnlyList<OnvifMediaProfile> Profiles,
    IReadOnlyDictionary<string, string> RawXAddrs);
