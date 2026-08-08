using System.Xml.Linq;
using Rcordr.Onvif.Soap;

namespace Rcordr.Onvif.Clients;

/// <summary>ONVIF ver10 media service — profile enumeration and RTSP stream/snapshot URIs. Only
/// the ver10 (Profile S) media service is implemented; ver20 media2 (Profile T) is picked up as a
/// capability flag by the prober but not yet queried for its own profile list — see the plan's note
/// that main/sub stream role assignment (M5) is where media2-specific configs would be added if a
/// camera needs them.</summary>
public class OnvifMediaClient(OnvifSoapClient soap)
{
    private const string MediaNs = "http://www.onvif.org/ver10/media/wsdl";
    private const string SchemaNs = "http://www.onvif.org/ver10/schema";

    public async Task<IReadOnlyList<OnvifMediaProfile>> GetProfilesAsync(
        Uri mediaServiceUri, OnvifCredentials? credentials, CancellationToken ct = default)
    {
        var body = $"""<GetProfiles xmlns="{MediaNs}"/>""";
        var response = await soap.PostAsync(mediaServiceUri, $"{MediaNs}/GetProfiles", body, credentials, ct);

        var profiles = new List<OnvifMediaProfile>();
        foreach (var p in response.Elements().Where(e => e.Name.LocalName == "Profiles"))
        {
            var token = p.Attribute("token")?.Value ?? string.Empty;
            var name = p.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value;

            var vec = p.Elements().FirstOrDefault(e => e.Name.LocalName == "VideoEncoderConfiguration");
            var encoding = vec?.Elements().FirstOrDefault(e => e.Name.LocalName == "Encoding")?.Value;
            var resolution = vec?.Elements().FirstOrDefault(e => e.Name.LocalName == "Resolution");
            int? width = int.TryParse(resolution?.Elements().FirstOrDefault(e => e.Name.LocalName == "Width")?.Value, out var w) ? w : null;
            int? height = int.TryParse(resolution?.Elements().FirstOrDefault(e => e.Name.LocalName == "Height")?.Value, out var h) ? h : null;
            var rateControl = vec?.Elements().FirstOrDefault(e => e.Name.LocalName == "RateControl");
            int? fps = int.TryParse(rateControl?.Elements().FirstOrDefault(e => e.Name.LocalName == "FrameRateLimit")?.Value, out var f) ? f : null;
            int? bitrate = int.TryParse(rateControl?.Elements().FirstOrDefault(e => e.Name.LocalName == "BitrateLimit")?.Value, out var b) ? b : null;

            var aec = p.Elements().FirstOrDefault(e => e.Name.LocalName == "AudioEncoderConfiguration");
            var audioEncoding = aec?.Elements().FirstOrDefault(e => e.Name.LocalName == "Encoding")?.Value;
            var hasAudioOutputConfig = p.Elements().Any(e => e.Name.LocalName == "AudioOutputConfiguration");

            if (string.IsNullOrEmpty(token)) continue;
            profiles.Add(new OnvifMediaProfile(token, name, encoding, width, height, fps, bitrate,
                audioEncoding, aec is not null, hasAudioOutputConfig));
        }
        return profiles;
    }

    public async Task<Uri?> GetStreamUriAsync(
        Uri mediaServiceUri, string profileToken, OnvifCredentials? credentials, CancellationToken ct = default)
    {
        var body = $"""
            <GetStreamUri xmlns="{MediaNs}">
              <StreamSetup>
                <Stream xmlns="{SchemaNs}">RTP-Unicast</Stream>
                <Transport xmlns="{SchemaNs}"><Protocol>RTSP</Protocol></Transport>
              </StreamSetup>
              <ProfileToken>{System.Security.SecurityElement.Escape(profileToken)}</ProfileToken>
            </GetStreamUri>
            """;
        var response = await soap.PostAsync(mediaServiceUri, $"{MediaNs}/GetStreamUri", body, credentials, ct);
        var uriText = response.Elements().FirstOrDefault(e => e.Name.LocalName == "MediaUri")?
            .Elements().FirstOrDefault(e => e.Name.LocalName == "Uri")?.Value;
        return uriText is not null && Uri.TryCreate(uriText, UriKind.Absolute, out var u) ? u : null;
    }

    public async Task<Uri?> GetSnapshotUriAsync(
        Uri mediaServiceUri, string profileToken, OnvifCredentials? credentials, CancellationToken ct = default)
    {
        var body = $"""
            <GetSnapshotUri xmlns="{MediaNs}">
              <ProfileToken>{System.Security.SecurityElement.Escape(profileToken)}</ProfileToken>
            </GetSnapshotUri>
            """;
        try
        {
            var response = await soap.PostAsync(mediaServiceUri, $"{MediaNs}/GetSnapshotUri", body, credentials, ct);
            var uriText = response.Elements().FirstOrDefault(e => e.Name.LocalName == "MediaUri")?
                .Elements().FirstOrDefault(e => e.Name.LocalName == "Uri")?.Value;
            return uriText is not null && Uri.TryCreate(uriText, UriKind.Absolute, out var u) ? u : null;
        }
        catch (OnvifFaultException)
        {
            // Not every profile/device supports snapshots — treat as "unavailable", not an error.
            return null;
        }
    }
}
