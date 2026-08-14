namespace LarisVMS.Core.Dtos;

/// <summary>One WS-Discovery result, before it becomes a Camera row. Mirrors
/// LarisVMS.Onvif.Discovery.DiscoveredDevice but lives in Core so Web doesn't need a reference to
/// LarisVMS.Onvif just to render the discovery page.</summary>
public record DiscoveredCameraDto(string EndpointReference, IReadOnlyList<string> XAddrs,
    string? HardwareHint, string? NameHint, string RemoteAddress);

public record AddCameraRequest(
    string Name, string DeviceServiceUri, string? Username, string? Password, Guid? GroupId);

public record CameraProbeSummary(
    bool ProfileS, bool ProfileT, bool ProfileG, bool ProfileM,
    bool HasPtz, bool HasImaging, bool HasEvents, bool HasAnalyticsMetadata,
    bool HasMedia2, bool HasAudioOut, bool HasRelayOutputs, bool HasDigitalInputs,
    int StreamCount, string? Error);
