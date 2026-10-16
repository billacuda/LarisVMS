namespace LarisVMS.Onvif.Clients;

public record OnvifDeviceInformation(
    string? Manufacturer, string? Model, string? FirmwareVersion, string? SerialNumber, string? HardwareId);

public record OnvifServiceEntry(string Namespace, Uri XAddr, int? MajorVersion, int? MinorVersion);

/// <summary>AudioOutputCount is DeviceIO's AudioOutputs count from GetCapabilities — how many speakers
/// or line-outs the device has (0 when it doesn't say).</summary>
public record OnvifCapabilitiesResult(
    Uri? MediaXAddr, Uri? PtzXAddr, Uri? ImagingXAddr, Uri? EventsXAddr,
    Uri? DeviceIOXAddr, Uri? AnalyticsXAddr, IReadOnlyDictionary<string, string> RawCategoryXAddrs,
    int AudioOutputCount = 0);
