using System.Xml.Linq;
using LarisVMS.Onvif.Soap;

namespace LarisVMS.Onvif.Clients;

/// <summary>ONVIF ver20 media2 (Profile T) service — video encoder configuration only. Media2 is the
/// standard way to read and set what Media1 can't express: H.265, CBR vs. VBR
/// (RateControl/@ConstantBitRate), and a discrete frame-rate list. Configuration tokens are shared with
/// the ver10 media service on real devices, so callers address a configuration by the token from the
/// Media1 profile list.</summary>
public class OnvifMedia2Client(OnvifSoapClient soap)
{
    private const string Media2Ns = "http://www.onvif.org/ver20/media/wsdl";

    private static readonly string[] ConfigOrder = ["Name", "UseCount", "Encoding", "Resolution", "RateControl", "Multicast", "Quality"];
    private static readonly string[] RateControlOrder = ["FrameRateLimit", "BitrateLimit"];
    private static readonly string[] ResolutionOrder = ["Width", "Height"];

    public async Task<OnvifVideoEncoderConfig?> GetVideoEncoderConfigurationAsync(
        Uri media2ServiceUri, string configurationToken, OnvifCredentials? credentials, CancellationToken ct = default)
    {
        var body = $"""
            <GetVideoEncoderConfigurations xmlns="{Media2Ns}">
              <ConfigurationToken>{Escape(configurationToken)}</ConfigurationToken>
            </GetVideoEncoderConfigurations>
            """;
        var response = await soap.PostAsync(media2ServiceUri, $"{Media2Ns}/GetVideoEncoderConfigurations", body, credentials, ct);
        var config = response.Children("Configurations").FirstOrDefault(c => c.Attribute("token")?.Value == configurationToken)
            ?? response.Child("Configurations");
        return config is null ? null : ParseConfig(config);
    }

    public async Task<IReadOnlyList<OnvifVideoEncodingOptions>> GetVideoEncoderConfigurationOptionsAsync(
        Uri media2ServiceUri, string configurationToken, string? profileToken, OnvifCredentials? credentials, CancellationToken ct = default)
    {
        var profileXml = profileToken is null ? "" : $"<ProfileToken>{Escape(profileToken)}</ProfileToken>";
        var body = $"""
            <GetVideoEncoderConfigurationOptions xmlns="{Media2Ns}">
              <ConfigurationToken>{Escape(configurationToken)}</ConfigurationToken>
              {profileXml}
            </GetVideoEncoderConfigurationOptions>
            """;
        var response = await soap.PostAsync(media2ServiceUri, $"{Media2Ns}/GetVideoEncoderConfigurationOptions", body, credentials, ct);
        return ParseOptions(response);
    }

    /// <summary>Media2's Set always persists (there's no ForcePersistence flag) and, like Media1's,
    /// replaces the whole configuration — so it's built from the configuration as received.</summary>
    public async Task SetVideoEncoderConfigurationAsync(
        Uri media2ServiceUri, OnvifVideoEncoderConfig current, OnvifVideoEncoderChange change,
        OnvifCredentials? credentials, CancellationToken ct = default)
    {
        var config = BuildConfigForSet(current, change);
        var body = $"""
            <SetVideoEncoderConfiguration xmlns="{Media2Ns}">
              {config}
            </SetVideoEncoderConfiguration>
            """;
        await soap.PostAsync(media2ServiceUri, $"{Media2Ns}/SetVideoEncoderConfiguration", body, credentials, ct);
    }

    internal static OnvifVideoEncoderConfig ParseConfig(XElement config)
    {
        var rate = config.Child("RateControl");
        var res = config.Child("Resolution");
        return new OnvifVideoEncoderConfig(
            Token: config.Attribute("token")?.Value ?? string.Empty,
            Name: config.Text("Name"),
            UseCount: config.Int("UseCount") ?? 0,
            Encoding: NormalizeEncoding(config.Text("Encoding")),
            Resolution: res.Int("Width") is { } w && res.Int("Height") is { } h ? new OnvifResolution(w, h) : null,
            Quality: config.Double("Quality"),
            FrameRateLimit: rate.Double("FrameRateLimit"),
            EncodingInterval: null,
            BitrateLimitKbps: rate.Int("BitrateLimit"),
            ConstantBitRate: rate?.Attribute("ConstantBitRate")?.Value is { } cbr ? cbr.Equals("true", StringComparison.OrdinalIgnoreCase) || cbr == "1" : null,
            GovLength: OnvifXml.ParseInt(config.Attribute("GovLength")?.Value),
            Profile: config.Attribute("Profile")?.Value)
        { Raw = new XElement(config) };
    }

    internal static IReadOnlyList<OnvifVideoEncodingOptions> ParseOptions(XElement response)
    {
        var result = new List<OnvifVideoEncodingOptions>();
        foreach (var o in response.Children("Options"))
        {
            var encoding = NormalizeEncoding(o.Text("Encoding"));
            if (string.IsNullOrEmpty(encoding)) continue;
            var gov = OnvifXml.SplitList(o.Attribute("GovLengthRange")?.Value).Select(OnvifXml.ParseDouble).OfType<double>().ToList();
            var resolutions = o.Children("ResolutionsAvailable")
                .Select(r => r.Int("Width") is { } w && r.Int("Height") is { } h ? new OnvifResolution(w, h) : null)
                .OfType<OnvifResolution>().ToList();
            var frameRates = OnvifXml.SplitList(o.Attribute("FrameRatesSupported")?.Value)
                .Select(OnvifXml.ParseDouble).OfType<double>().Distinct().OrderByDescending(f => f).ToList();
            result.Add(new OnvifVideoEncodingOptions(
                encoding, resolutions,
                FrameRateRange: null,
                FrameRatesSupported: frameRates,
                BitrateRangeKbps: o.Range("BitrateRange"),
                GovLengthRange: gov.Count >= 2 ? new OnvifRange(gov.Min(), gov.Max()) : null,
                QualityRange: o.Range("QualityRange"),
                Profiles: OnvifXml.SplitList(o.Attribute("ProfilesSupported")?.Value),
                ConstantBitRateSupported: o.Attribute("ConstantBitRateSupported")?.Value is { } c
                    && (c.Equals("true", StringComparison.OrdinalIgnoreCase) || c == "1")));
        }

        // Some devices list an encoding twice (e.g. once per profile family); merge them so the UI
        // offers one codec choice with the union of what each entry allows.
        return result.GroupBy(r => r.Encoding).Select(g => g.Count() == 1 ? g.First() : g.Aggregate(Merge)).ToList();
    }

    private static OnvifVideoEncodingOptions Merge(OnvifVideoEncodingOptions a, OnvifVideoEncodingOptions b) => a with
    {
        Resolutions = a.Resolutions.Concat(b.Resolutions).Distinct().ToList(),
        FrameRatesSupported = a.FrameRatesSupported.Concat(b.FrameRatesSupported).Distinct().OrderByDescending(f => f).ToList(),
        BitrateRangeKbps = a.BitrateRangeKbps ?? b.BitrateRangeKbps,
        GovLengthRange = a.GovLengthRange ?? b.GovLengthRange,
        QualityRange = a.QualityRange ?? b.QualityRange,
        Profiles = a.Profiles.Concat(b.Profiles).Distinct().ToList(),
        ConstantBitRateSupported = a.ConstantBitRateSupported || b.ConstantBitRateSupported,
    };

    internal static XElement BuildConfigForSet(OnvifVideoEncoderConfig current, OnvifVideoEncoderChange change)
    {
        var config = new XElement(XName.Get("Configuration", Media2Ns), current.Raw.Attributes(), current.Raw.Nodes());
        if (change.Encoding is not null) config.SetChild("Encoding", change.Encoding == "MPEG4" ? "MPV4-ES" : change.Encoding, ConfigOrder);
        if (change.Resolution is { } r)
        {
            var res = config.SetChild("Resolution", null, ConfigOrder);
            res.SetChild("Width", OnvifXml.Format(r.Width), ResolutionOrder);
            res.SetChild("Height", OnvifXml.Format(r.Height), ResolutionOrder);
        }
        if (change.FrameRateLimit is not null || change.BitrateLimitKbps is not null || change.ConstantBitRate is not null)
        {
            var rate = config.SetChild("RateControl", null, ConfigOrder);
            if (change.FrameRateLimit is { } f) rate.SetChild("FrameRateLimit", OnvifXml.Format(f), RateControlOrder);
            if (change.BitrateLimitKbps is { } b) rate.SetChild("BitrateLimit", OnvifXml.Format(b), RateControlOrder);
            if (change.ConstantBitRate is { } cbr) rate.SetAttributeValue("ConstantBitRate", OnvifXml.Format(cbr));
        }
        if (change.Quality is { } q) config.SetChild("Quality", OnvifXml.Format(q), ConfigOrder);
        if (change.GovLength is { } g) config.SetAttributeValue("GovLength", OnvifXml.Format(g));
        if (change.Profile is not null) config.SetAttributeValue("Profile", change.Profile);
        // A profile name belongs to one codec family (H.264 "High" means nothing to JPEG); clear a stale
        // one when switching codec without choosing a new profile.
        else if (change.Encoding is not null && change.Encoding != current.Encoding) config.Attribute("Profile")?.Remove();
        return config;
    }

    /// <summary>Media2 spells MPEG-4 "MPV4-ES"; everything else matches Media1's names.</summary>
    private static string? NormalizeEncoding(string? encoding) => encoding switch
    {
        null => null,
        "MPV4-ES" => "MPEG4",
        _ => encoding.ToUpperInvariant(),
    };

    private static string Escape(string s) => System.Security.SecurityElement.Escape(s);
}
