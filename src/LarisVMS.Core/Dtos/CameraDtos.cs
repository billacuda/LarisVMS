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
