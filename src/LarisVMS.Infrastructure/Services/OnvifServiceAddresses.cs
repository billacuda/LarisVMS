using System.Text.Json;
using LarisVMS.Onvif.Soap;

namespace LarisVMS.Infrastructure.Services;

/// <summary>Reads a camera's per-service ONVIF XAddrs out of CameraCapabilities.RawProbeJson (the
/// category → XAddr map the prober stores: "Media", "Media2", "PTZ", "Imaging", "Events", …), and
/// builds its credentials. Shared by every service that calls the camera directly from Web.</summary>
internal static class OnvifServiceAddresses
{
    /// <summary>Null when the JSON is missing or corrupt, or has no valid absolute URI under
    /// <paramref name="key"/>. A pre-existing or corrupt RawProbeJson is treated as "not resolvable",
    /// never as an error.</summary>
    public static Uri? Get(string? rawProbeJson, string key)
    {
        if (string.IsNullOrEmpty(rawProbeJson)) return null;
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(rawProbeJson);
            return map?.GetValueOrDefault(key) is { } text && Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Username/Password decrypt transparently through EF's EncryptedNullableStringConverter.</summary>
    public static OnvifCredentials? Credentials(string? username, string? password)
        => username is null ? null : new OnvifCredentials(username, password ?? string.Empty);
}
