namespace Rcordr.Onvif.Clients;

public record OnvifDeviceInformation(
    string? Manufacturer, string? Model, string? FirmwareVersion, string? SerialNumber, string? HardwareId);

public record OnvifServiceEntry(string Namespace, Uri XAddr, int? MajorVersion, int? MinorVersion);

public record OnvifCapabilitiesResult(
    Uri? MediaXAddr, Uri? PtzXAddr, Uri? ImagingXAddr, Uri? EventsXAddr,
    Uri? DeviceIOXAddr, Uri? AnalyticsXAddr, IReadOnlyDictionary<string, string> RawCategoryXAddrs);
