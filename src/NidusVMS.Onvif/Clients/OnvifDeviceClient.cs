using System.Xml.Linq;
using NidusVMS.Onvif.Soap;

namespace NidusVMS.Onvif.Clients;

/// <summary>ONVIF ver10 device management service (GetDeviceInformation / GetCapabilities /
/// GetServices) — the entry point for everything else the capability prober needs.</summary>
public class OnvifDeviceClient(OnvifSoapClient soap)
{
    private const string DeviceNs = "http://www.onvif.org/ver10/device/wsdl";
    private static readonly XNamespace Dev = DeviceNs;

    public async Task<OnvifDeviceInformation> GetDeviceInformationAsync(
        Uri deviceServiceUri, OnvifCredentials? credentials, CancellationToken ct = default)
    {
        var body = $"""<GetDeviceInformation xmlns="{DeviceNs}"/>""";
        var response = await soap.PostAsync(deviceServiceUri, $"{DeviceNs}/GetDeviceInformation", body, credentials, ct);

        return new OnvifDeviceInformation(
            response.Element(Dev + "Manufacturer")?.Value,
            response.Element(Dev + "Model")?.Value,
            response.Element(Dev + "FirmwareVersion")?.Value,
            response.Element(Dev + "SerialNumber")?.Value,
            response.Element(Dev + "HardwareId")?.Value);
    }

    public async Task<OnvifCapabilitiesResult> GetCapabilitiesAsync(
        Uri deviceServiceUri, OnvifCredentials? credentials, CancellationToken ct = default)
    {
        var body = $"""<GetCapabilities xmlns="{DeviceNs}"><Category>All</Category></GetCapabilities>""";
        var response = await soap.PostAsync(deviceServiceUri, $"{DeviceNs}/GetCapabilities", body, credentials, ct);

        var caps = response.Element(Dev + "Capabilities");
        var raw = new Dictionary<string, string>();
        Uri? XAddrOf(string categoryLocalName)
        {
            // Category elements (Media, PTZ, Imaging, Events, Analytics, Extension/DeviceIO) come
            // back in whatever namespace the device declares them in — ver10 schema, not the device
            // wsdl namespace — so match by local name rather than a fixed XNamespace.
            var element = caps?.Elements().FirstOrDefault(e => e.Name.LocalName == categoryLocalName)
                ?? caps?.Element(Dev + "Extension")?.Elements().FirstOrDefault(e => e.Name.LocalName == categoryLocalName);
            var xAddrText = element?.Elements().FirstOrDefault(e => e.Name.LocalName == "XAddr")?.Value;
            if (xAddrText is not null) raw[categoryLocalName] = xAddrText;
            return xAddrText is not null && Uri.TryCreate(xAddrText, UriKind.Absolute, out var u) ? u : null;
        }

        return new OnvifCapabilitiesResult(
            XAddrOf("Media"), XAddrOf("PTZ"), XAddrOf("Imaging"), XAddrOf("Events"),
            XAddrOf("DeviceIO"), XAddrOf("Analytics"), raw);
    }

    public async Task<IReadOnlyList<OnvifServiceEntry>> GetServicesAsync(
        Uri deviceServiceUri, OnvifCredentials? credentials, CancellationToken ct = default)
    {
        var body = $"""<GetServices xmlns="{DeviceNs}"><IncludeCapability>false</IncludeCapability></GetServices>""";
        var response = await soap.PostAsync(deviceServiceUri, $"{DeviceNs}/GetServices", body, credentials, ct);

        var entries = new List<OnvifServiceEntry>();
        foreach (var service in response.Elements().Where(e => e.Name.LocalName == "Service"))
        {
            var ns = service.Elements().FirstOrDefault(e => e.Name.LocalName == "Namespace")?.Value;
            var xAddrText = service.Elements().FirstOrDefault(e => e.Name.LocalName == "XAddr")?.Value;
            if (ns is null || xAddrText is null || !Uri.TryCreate(xAddrText, UriKind.Absolute, out var xAddr))
                continue;

            var version = service.Elements().FirstOrDefault(e => e.Name.LocalName == "Version");
            int? major = int.TryParse(version?.Elements().FirstOrDefault(e => e.Name.LocalName == "Major")?.Value, out var maj) ? maj : null;
            int? minor = int.TryParse(version?.Elements().FirstOrDefault(e => e.Name.LocalName == "Minor")?.Value, out var min) ? min : null;

            entries.Add(new OnvifServiceEntry(ns, xAddr, major, minor));
        }
        return entries;
    }
}
