namespace NidusVMS.Onvif.Discovery;

/// <summary>One WS-Discovery ProbeMatch. XAddrs is usually one device-service URL, but a
/// multi-homed camera can advertise more than one — the caller picks the one reachable from this
/// host.</summary>
public record DiscoveredDevice(
    string EndpointReference,
    IReadOnlyList<Uri> XAddrs,
    IReadOnlyList<string> Types,
    IReadOnlyList<string> Scopes,
    string RemoteAddress)
{
    /// <summary>Scopes carry vendor/hardware hints as URIs, e.g.
    /// "onvif://www.onvif.org/hardware/DS-2CD2385" or "onvif://www.onvif.org/name/Hallway-Cam".
    /// Best-effort extraction — not every vendor populates these consistently.</summary>
    public string? HardwareHint => ExtractScope("hardware");
    public string? NameHint => ExtractScope("name");

    private string? ExtractScope(string category)
    {
        var prefix = $"onvif://www.onvif.org/{category}/";
        var match = Scopes.FirstOrDefault(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return match is null ? null : Uri.UnescapeDataString(match[prefix.Length..]);
    }
}
