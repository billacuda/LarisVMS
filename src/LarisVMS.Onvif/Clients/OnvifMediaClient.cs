using System.Xml.Linq;
using LarisVMS.Onvif.Soap;

namespace LarisVMS.Onvif.Clients;

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
            // In the ONVIF schema a profile's audio output (and decoder) configuration sit inside its
            // <Extension> element, not directly under the profile — looking only at direct children
            // missed every compliant camera's speaker.
            var hasAudioOutputConfig = p.Elements().Any(e => e.Name.LocalName == "AudioOutputConfiguration")
                || p.Elements().Where(e => e.Name.LocalName == "Extension")
                    .Elements().Any(e => e.Name.LocalName == "AudioOutputConfiguration");

            // Which physical sensor this profile draws from — see OnvifMediaProfile's own comment.
            // The SourceToken *inside* VideoSourceConfiguration, not the configuration element's own
            // token attribute: the latter identifies the configuration, and a device can point several
            // differently-tokened configurations at the same physical source.
            var vsc = p.Elements().FirstOrDefault(e => e.Name.LocalName == "VideoSourceConfiguration");
            var videoSourceToken = vsc?.Elements().FirstOrDefault(e => e.Name.LocalName == "SourceToken")?.Value;

            if (string.IsNullOrEmpty(token)) continue;
            profiles.Add(new OnvifMediaProfile(token, name, encoding, width, height, fps, bitrate,
                audioEncoding, aec is not null, hasAudioOutputConfig,
                string.IsNullOrWhiteSpace(videoSourceToken) ? null : videoSourceToken,
                vec?.Attribute("token")?.Value is { Length: > 0 } vecToken ? vecToken : null,
                aec?.Attribute("token")?.Value is { Length: > 0 } aecToken ? aecToken : null));
        }
        return profiles;
    }

    /// <summary>How many audio outputs (speakers / line-outs) the media service reports, whether or not
    /// any profile has one attached yet. 0 when the device doesn't implement the call.</summary>
    public async Task<int> GetAudioOutputCountAsync(Uri mediaServiceUri, OnvifCredentials? credentials, CancellationToken ct = default)
    {
        try
        {
            var body = $"""<GetAudioOutputs xmlns="{MediaNs}"/>""";
            var response = await soap.PostAsync(mediaServiceUri, $"{MediaNs}/GetAudioOutputs", body, credentials, ct);
            return response.Elements().Count(e => e.Name.LocalName == "AudioOutputs");
        }
        catch (OnvifFaultException)
        {
            return 0;
        }
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

    // ── Encoder configuration (get / options / set) ──────────────────────────────────────────────

    private static readonly string[] VideoConfigOrder =
        ["Name", "UseCount", "Encoding", "Resolution", "Quality", "RateControl", "MPEG4", "H264", "Multicast", "SessionTimeout", "Extension"];
    private static readonly string[] RateControlOrder = ["FrameRateLimit", "EncodingInterval", "BitrateLimit"];
    private static readonly string[] ResolutionOrder = ["Width", "Height"];
    private static readonly string[] AudioConfigOrder =
        ["Name", "UseCount", "Encoding", "Bitrate", "SampleRate", "Multicast", "SessionTimeout"];

    public async Task<OnvifVideoEncoderConfig> GetVideoEncoderConfigurationAsync(
        Uri mediaServiceUri, string configurationToken, OnvifCredentials? credentials, CancellationToken ct = default)
    {
        var body = $"""
            <GetVideoEncoderConfiguration xmlns="{MediaNs}">
              <ConfigurationToken>{Escape(configurationToken)}</ConfigurationToken>
            </GetVideoEncoderConfiguration>
            """;
        var response = await soap.PostAsync(mediaServiceUri, $"{MediaNs}/GetVideoEncoderConfiguration", body, credentials, ct);
        var config = response.Child("Configuration") ?? throw new OnvifFaultException("Response had no Configuration.", null);
        return ParseVideoConfig(config);
    }

    public async Task<IReadOnlyList<OnvifVideoEncodingOptions>> GetVideoEncoderConfigurationOptionsAsync(
        Uri mediaServiceUri, string configurationToken, string? profileToken, OnvifCredentials? credentials, CancellationToken ct = default)
    {
        var profileXml = profileToken is null ? "" : $"<ProfileToken>{Escape(profileToken)}</ProfileToken>";
        var body = $"""
            <GetVideoEncoderConfigurationOptions xmlns="{MediaNs}">
              <ConfigurationToken>{Escape(configurationToken)}</ConfigurationToken>
              {profileXml}
            </GetVideoEncoderConfigurationOptions>
            """;
        var response = await soap.PostAsync(mediaServiceUri, $"{MediaNs}/GetVideoEncoderConfigurationOptions", body, credentials, ct);
        return ParseVideoOptions(response.Child("Options"));
    }

    /// <summary>Applies <paramref name="change"/> on top of <paramref name="current"/> (as returned by
    /// <see cref="GetVideoEncoderConfigurationAsync"/>) and saves it with ForcePersistence. Media1's Set
    /// replaces the whole configuration, so everything not being changed is sent back exactly as the
    /// device reported it.</summary>
    public async Task SetVideoEncoderConfigurationAsync(
        Uri mediaServiceUri, OnvifVideoEncoderConfig current, OnvifVideoEncoderChange change,
        OnvifCredentials? credentials, CancellationToken ct = default)
    {
        var config = BuildVideoConfigForSet(current, change);
        var body = $"""
            <SetVideoEncoderConfiguration xmlns="{MediaNs}">
              {config}
              <ForcePersistence>true</ForcePersistence>
            </SetVideoEncoderConfiguration>
            """;
        await soap.PostAsync(mediaServiceUri, $"{MediaNs}/SetVideoEncoderConfiguration", body, credentials, ct);
    }

    public async Task<OnvifAudioEncoderConfig> GetAudioEncoderConfigurationAsync(
        Uri mediaServiceUri, string configurationToken, OnvifCredentials? credentials, CancellationToken ct = default)
    {
        var body = $"""
            <GetAudioEncoderConfiguration xmlns="{MediaNs}">
              <ConfigurationToken>{Escape(configurationToken)}</ConfigurationToken>
            </GetAudioEncoderConfiguration>
            """;
        var response = await soap.PostAsync(mediaServiceUri, $"{MediaNs}/GetAudioEncoderConfiguration", body, credentials, ct);
        var config = response.Child("Configuration") ?? throw new OnvifFaultException("Response had no Configuration.", null);
        return ParseAudioConfig(config);
    }

    public async Task<IReadOnlyList<OnvifAudioEncodingOptions>> GetAudioEncoderConfigurationOptionsAsync(
        Uri mediaServiceUri, string configurationToken, string? profileToken, OnvifCredentials? credentials, CancellationToken ct = default)
    {
        var profileXml = profileToken is null ? "" : $"<ProfileToken>{Escape(profileToken)}</ProfileToken>";
        var body = $"""
            <GetAudioEncoderConfigurationOptions xmlns="{MediaNs}">
              <ConfigurationToken>{Escape(configurationToken)}</ConfigurationToken>
              {profileXml}
            </GetAudioEncoderConfigurationOptions>
            """;
        var response = await soap.PostAsync(mediaServiceUri, $"{MediaNs}/GetAudioEncoderConfigurationOptions", body, credentials, ct);
        return ParseAudioOptions(response.Child("Options"));
    }

    public async Task SetAudioEncoderConfigurationAsync(
        Uri mediaServiceUri, OnvifAudioEncoderConfig current, OnvifAudioEncoderChange change,
        OnvifCredentials? credentials, CancellationToken ct = default)
    {
        var config = BuildAudioConfigForSet(current, change);
        var body = $"""
            <SetAudioEncoderConfiguration xmlns="{MediaNs}">
              {config}
              <ForcePersistence>true</ForcePersistence>
            </SetAudioEncoderConfiguration>
            """;
        await soap.PostAsync(mediaServiceUri, $"{MediaNs}/SetAudioEncoderConfiguration", body, credentials, ct);
    }

    internal static OnvifVideoEncoderConfig ParseVideoConfig(XElement config)
    {
        var codecBlock = config.Child("H264") ?? config.Child("MPEG4");
        var rate = config.Child("RateControl");
        var res = config.Child("Resolution");
        return new OnvifVideoEncoderConfig(
            Token: config.Attribute("token")?.Value ?? string.Empty,
            Name: config.Text("Name"),
            UseCount: config.Int("UseCount") ?? 0,
            Encoding: config.Text("Encoding"),
            Resolution: res.Int("Width") is { } w && res.Int("Height") is { } h ? new OnvifResolution(w, h) : null,
            Quality: config.Double("Quality"),
            FrameRateLimit: rate.Double("FrameRateLimit"),
            EncodingInterval: rate.Int("EncodingInterval"),
            BitrateLimitKbps: rate.Int("BitrateLimit"),
            ConstantBitRate: null,
            GovLength: codecBlock.Int("GovLength"),
            Profile: codecBlock.Text("H264Profile") ?? codecBlock.Text("Mpeg4Profile"))
        { Raw = new XElement(config) };
    }

    internal static IReadOnlyList<OnvifVideoEncodingOptions> ParseVideoOptions(XElement? options)
    {
        var result = new List<OnvifVideoEncodingOptions>();
        if (options is null) return result;
        var quality = options.Range("QualityRange");
        var extension = options.Child("Extension");
        foreach (var encoding in new[] { "H264", "MPEG4", "JPEG" })
        {
            var block = options.Child(encoding);
            if (block is null) continue;
            var resolutions = block.Children("ResolutionsAvailable")
                .Select(r => r.Int("Width") is { } w && r.Int("Height") is { } h ? new OnvifResolution(w, h) : null)
                .OfType<OnvifResolution>().ToList();
            var profiles = block.Children("H264ProfilesSupported").Concat(block.Children("Mpeg4ProfilesSupported"))
                .Select(p => p.Value.Trim()).Where(p => p.Length > 0).Distinct().ToList();
            result.Add(new OnvifVideoEncodingOptions(
                encoding, resolutions,
                FrameRateRange: block.Range("FrameRateRange"),
                FrameRatesSupported: [],
                BitrateRangeKbps: extension.Child(encoding).Range("BitrateRange") ?? block.Range("BitrateRange"),
                GovLengthRange: block.Range("GovLengthRange"),
                QualityRange: quality,
                Profiles: profiles,
                ConstantBitRateSupported: false));
        }
        return result;
    }

    internal static XElement BuildVideoConfigForSet(OnvifVideoEncoderConfig current, OnvifVideoEncoderChange change)
    {
        var config = new XElement(XName.Get("Configuration", MediaNs), current.Raw.Attributes(), current.Raw.Nodes());
        var encoding = change.Encoding ?? current.Encoding;
        if (change.Encoding is not null) config.SetChild("Encoding", change.Encoding, VideoConfigOrder);
        if (change.Resolution is { } r)
        {
            var res = config.SetChild("Resolution", null, VideoConfigOrder);
            res.SetChild("Width", OnvifXml.Format(r.Width), ResolutionOrder);
            res.SetChild("Height", OnvifXml.Format(r.Height), ResolutionOrder);
        }
        if (change.Quality is { } q) config.SetChild("Quality", OnvifXml.Format(q), VideoConfigOrder);
        if (change.FrameRateLimit is not null || change.BitrateLimitKbps is not null)
        {
            var rate = config.SetChild("RateControl", null, VideoConfigOrder);
            // Media1 types FrameRateLimit as xs:int.
            if (change.FrameRateLimit is { } f) rate.SetChild("FrameRateLimit", OnvifXml.Format((int)Math.Round(f)), RateControlOrder);
            if (change.BitrateLimitKbps is { } b) rate.SetChild("BitrateLimit", OnvifXml.Format(b), RateControlOrder);
        }

        // GovLength and profile live in a per-codec block (H264 or MPEG4). Switching codec needs that
        // block present, and both its children are required by the schema, so a missing one gets a
        // safe default rather than an invalid request.
        if (encoding is "H264" or "MPEG4")
        {
            var profileName = encoding == "H264" ? "H264Profile" : "Mpeg4Profile";
            var block = config.Child(encoding);
            if (block is null || change.GovLength is not null || change.Profile is not null)
            {
                string[] order = ["GovLength", profileName];
                block ??= config.SetChild(encoding, null, VideoConfigOrder);
                block.SetChild("GovLength", change.GovLength is { } g ? OnvifXml.Format(g)
                    : block.Text("GovLength") ?? OnvifXml.Format(current.GovLength ?? 30), order);
                block.SetChild(profileName, change.Profile ?? block.Text(profileName) ?? (encoding == "H264" ? "Main" : "SP"), order);
            }
        }
        return config;
    }

    internal static XElement BuildAudioConfigForSet(OnvifAudioEncoderConfig current, OnvifAudioEncoderChange change)
    {
        var config = new XElement(XName.Get("Configuration", MediaNs), current.Raw.Attributes(), current.Raw.Nodes());
        if (change.Encoding is not null) config.SetChild("Encoding", change.Encoding, AudioConfigOrder);
        if (change.BitrateKbps is { } b) config.SetChild("Bitrate", OnvifXml.Format(b), AudioConfigOrder);
        if (change.SampleRateKhz is { } r) config.SetChild("SampleRate", OnvifXml.Format(r), AudioConfigOrder);
        return config;
    }

    internal static OnvifAudioEncoderConfig ParseAudioConfig(XElement config) => new(
        Token: config.Attribute("token")?.Value ?? string.Empty,
        Name: config.Text("Name"),
        Encoding: config.Text("Encoding"),
        BitrateKbps: config.Int("Bitrate"),
        SampleRateKhz: config.Int("SampleRate"))
    { Raw = new XElement(config) };

    internal static IReadOnlyList<OnvifAudioEncodingOptions> ParseAudioOptions(XElement? options)
        => options.Children("Options")
            .Select(o => (Encoding: o.Text("Encoding"), Element: o))
            .Where(o => !string.IsNullOrEmpty(o.Encoding))
            .Select(o => new OnvifAudioEncodingOptions(o.Encoding!, o.Element.Child("BitrateList").Items(), o.Element.Child("SampleRateList").Items()))
            .ToList();

    private static string Escape(string s) => System.Security.SecurityElement.Escape(s);
}
