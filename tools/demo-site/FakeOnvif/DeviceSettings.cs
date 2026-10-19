using System.Collections.Concurrent;
using System.Globalization;
using System.Xml.Linq;

namespace FakeOnvif;

/// <summary>In-memory encoder, imaging, and audio settings for each fake camera, so LarisVMS's Device
/// settings page can read and change them (GetProfiles reports them too, so a re-probe sees the
/// change). The video itself comes from MediaMTX and doesn't change. Settings reset when FakeOnvif
/// restarts.
///
/// To exercise the page's "hide what isn't supported" logic, cameras differ: an H.265 camera offers
/// Media2 (H.265, CBR/VBR, a frame-rate list) while an H.264 camera offers Media1 only (no H.265, no
/// CBR/VBR choice). No camera offers backlight compensation or exposure modes.</summary>
public sealed class DeviceSettingsStore
{
    private readonly ConcurrentDictionary<string, CameraSettings> _cameras = new();

    public CameraSettings For(CameraConfig camera) => _cameras.GetOrAdd(camera.Id, _ => CameraSettings.Defaults(camera));
}

public sealed class EncoderSettings
{
    public required string Token { get; init; }
    public required string Name { get; init; }
    public string Codec { get; set; } = "H264";
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    public int Kbps { get; set; }
    public bool Cbr { get; set; }
    public int Gov { get; set; } = 50;
    public string Profile { get; set; } = "Main";
    public double Quality { get; set; } = 4;
}

public sealed class CameraSettings
{
    public required bool Media2 { get; init; }
    public required Dictionary<string, EncoderSettings> Encoders { get; init; } // by profile token
    public double Brightness { get; set; } = 50;
    public double Contrast { get; set; } = 50;
    public double Saturation { get; set; } = 50;
    public double Sharpness { get; set; } = 50;
    public string IrCut { get; set; } = "AUTO";
    public string Wdr { get; set; } = "OFF";
    public double WdrLevel { get; set; } = 50;
    public string WhiteBalance { get; set; } = "AUTO";
    public double CrGain { get; set; } = 128;
    public double CbGain { get; set; } = 128;
    public string AudioCodec { get; set; } = "AAC";
    public int AudioKbps { get; set; } = 48;
    public int AudioKhz { get; set; } = 16;

    public static CameraSettings Defaults(CameraConfig c) => new()
    {
        Media2 = c.Codec == "H265",
        Encoders = new()
        {
            [Soap.MainToken] = new EncoderSettings
            {
                Token = "VideoEncoder_" + Soap.MainToken, Name = "MainStream", Codec = c.Codec,
                Width = 1920, Height = 1080, Fps = 25, Kbps = c.Codec == "H265" ? 3072 : 4096,
            },
            [Soap.SubToken] = new EncoderSettings
            {
                Token = "VideoEncoder_" + Soap.SubToken, Name = "SubStream", Codec = "H264",
                Width = 640, Height = 360, Fps = 15, Kbps = 512, Gov = 30,
            },
        },
    };

    public EncoderSettings? ByConfigToken(string? token) => Encoders.Values.FirstOrDefault(e => e.Token == token);
}

/// <summary>Response bodies and Set handling for the device-settings operations.</summary>
public static class DeviceSettingsSoap
{
    private static readonly (int W, int H)[] MainResolutions = [(1920, 1080), (1280, 720), (704, 480)];
    private static readonly (int W, int H)[] SubResolutions = [(640, 360), (704, 480), (352, 240)];

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static (int W, int H)[] ResolutionsFor(EncoderSettings e) => e.Name == "MainStream" ? MainResolutions : SubResolutions;

    // ── Media1 ──────────────────────────────────────────────────────────────────

    public static string Media1Config(EncoderSettings e, string element = "trt:Configuration") => $"""
        <{element} token="{e.Token}">
          <tt:Name>{e.Name}</tt:Name>
          <tt:UseCount>1</tt:UseCount>
          <tt:Encoding>{e.Codec}</tt:Encoding>
          <tt:Resolution><tt:Width>{e.Width}</tt:Width><tt:Height>{e.Height}</tt:Height></tt:Resolution>
          <tt:Quality>{F(e.Quality)}</tt:Quality>
          <tt:RateControl>
            <tt:FrameRateLimit>{(int)Math.Round(e.Fps)}</tt:FrameRateLimit>
            <tt:EncodingInterval>1</tt:EncodingInterval>
            <tt:BitrateLimit>{e.Kbps}</tt:BitrateLimit>
          </tt:RateControl>
          <tt:H264><tt:GovLength>{e.Gov}</tt:GovLength><tt:H264Profile>{e.Profile}</tt:H264Profile></tt:H264>
          <tt:Multicast>
            <tt:Address><tt:Type>IPv4</tt:Type><tt:IPv4Address>0.0.0.0</tt:IPv4Address></tt:Address>
            <tt:Port>0</tt:Port><tt:TTL>1</tt:TTL><tt:AutoStart>false</tt:AutoStart>
          </tt:Multicast>
          <tt:SessionTimeout>PT60S</tt:SessionTimeout>
        </{element}>
        """;

    public static string GetVideoEncoderConfiguration(CameraSettings s, string? token)
        => s.ByConfigToken(token) is { } e
            ? $"<trt:GetVideoEncoderConfigurationResponse>{Media1Config(e)}</trt:GetVideoEncoderConfigurationResponse>"
            : null!;

    public static string Media1Options(CameraSettings s, string? token)
    {
        if (s.ByConfigToken(token) is not { } e) return null!;
        var res = string.Concat(ResolutionsFor(e).Select(r => $"<tt:ResolutionsAvailable><tt:Width>{r.W}</tt:Width><tt:Height>{r.H}</tt:Height></tt:ResolutionsAvailable>"));
        return $"""
            <trt:GetVideoEncoderConfigurationOptionsResponse>
              <trt:Options>
                <tt:QualityRange><tt:Min>1</tt:Min><tt:Max>6</tt:Max></tt:QualityRange>
                <tt:JPEG>{res}<tt:FrameRateRange><tt:Min>1</tt:Min><tt:Max>15</tt:Max></tt:FrameRateRange><tt:EncodingIntervalRange><tt:Min>1</tt:Min><tt:Max>1</tt:Max></tt:EncodingIntervalRange></tt:JPEG>
                <tt:H264>{res}
                  <tt:GovLengthRange><tt:Min>1</tt:Min><tt:Max>150</tt:Max></tt:GovLengthRange>
                  <tt:FrameRateRange><tt:Min>1</tt:Min><tt:Max>{(e.Name == "MainStream" ? 30 : 15)}</tt:Max></tt:FrameRateRange>
                  <tt:EncodingIntervalRange><tt:Min>1</tt:Min><tt:Max>1</tt:Max></tt:EncodingIntervalRange>
                  <tt:H264ProfilesSupported>Baseline</tt:H264ProfilesSupported>
                  <tt:H264ProfilesSupported>Main</tt:H264ProfilesSupported>
                  <tt:H264ProfilesSupported>High</tt:H264ProfilesSupported>
                </tt:H264>
                <tt:Extension>
                  <tt:H264><tt:BitrateRange><tt:Min>64</tt:Min><tt:Max>{(e.Name == "MainStream" ? 8192 : 2048)}</tt:Max></tt:BitrateRange></tt:H264>
                </tt:Extension>
              </trt:Options>
            </trt:GetVideoEncoderConfigurationOptionsResponse>
            """;
    }

    // ── Media2 ──────────────────────────────────────────────────────────────────

    private static string Media2Config(EncoderSettings e) => $"""
        <tr2:Configurations token="{e.Token}" GovLength="{e.Gov}" Profile="{e.Profile}">
          <tt:Name>{e.Name}</tt:Name>
          <tt:UseCount>1</tt:UseCount>
          <tt:Encoding>{e.Codec}</tt:Encoding>
          <tt:Resolution><tt:Width>{e.Width}</tt:Width><tt:Height>{e.Height}</tt:Height></tt:Resolution>
          <tt:RateControl ConstantBitRate="{(e.Cbr ? "true" : "false")}">
            <tt:FrameRateLimit>{F(e.Fps)}</tt:FrameRateLimit>
            <tt:BitrateLimit>{e.Kbps}</tt:BitrateLimit>
          </tt:RateControl>
          <tt:Quality>{F(e.Quality)}</tt:Quality>
        </tr2:Configurations>
        """;

    public static string GetVideoEncoderConfigurations(CameraSettings s, string? token)
    {
        IEnumerable<EncoderSettings> configs = token is null ? s.Encoders.Values : s.ByConfigToken(token) is { } e ? [e] : [];
        return $"<tr2:GetVideoEncoderConfigurationsResponse>{string.Concat(configs.Select(Media2Config))}</tr2:GetVideoEncoderConfigurationsResponse>";
    }

    public static string Media2Options(CameraSettings s, string? token)
    {
        if (s.ByConfigToken(token) is not { } e) return null!;
        var res = string.Concat(ResolutionsFor(e).Select(r => $"<tt:ResolutionsAvailable><tt:Width>{r.W}</tt:Width><tt:Height>{r.H}</tt:Height></tt:ResolutionsAvailable>"));
        var rates = e.Name == "MainStream" ? "30 25 20 15 10 5" : "15 10 5";
        var maxKbps = e.Name == "MainStream" ? 8192 : 2048;
        string Option(string codec, string profiles) => $"""
            <tr2:Options GovLengthRange="1 150" FrameRatesSupported="{rates}" ProfilesSupported="{profiles}" ConstantBitRateSupported="true">
              <tt:Encoding>{codec}</tt:Encoding>
              <tt:QualityRange><tt:Min>1</tt:Min><tt:Max>6</tt:Max></tt:QualityRange>
              {res}
              <tt:BitrateRange><tt:Min>64</tt:Min><tt:Max>{maxKbps}</tt:Max></tt:BitrateRange>
            </tr2:Options>
            """;
        return $"""
            <tr2:GetVideoEncoderConfigurationOptionsResponse>
              {Option("H264", "Baseline Main High")}
              {Option("H265", "Main")}
            </tr2:GetVideoEncoderConfigurationOptionsResponse>
            """;
    }

    // ── Set (Media1 and Media2) ─────────────────────────────────────────────────

    public static string SetVideoEncoderConfiguration(CameraSettings s, XElement operation, bool media2)
    {
        var config = Child(operation, "Configuration");
        if (s.ByConfigToken(config?.Attribute("token")?.Value) is not { } e) return null!;

        if (Text(config, "Encoding") is { } codec) e.Codec = codec;
        var res = Child(config, "Resolution");
        if (Int(res, "Width") is { } w && Int(res, "Height") is { } h) { e.Width = w; e.Height = h; }
        var rate = Child(config, "RateControl");
        if (Double(rate, "FrameRateLimit") is { } fps) e.Fps = fps;
        if (Int(rate, "BitrateLimit") is { } kbps) e.Kbps = kbps;
        if (Double(config, "Quality") is { } q) e.Quality = q;
        if (media2)
        {
            if (rate?.Attribute("ConstantBitRate")?.Value is { } cbr) e.Cbr = cbr == "true";
            if (int.TryParse(config?.Attribute("GovLength")?.Value, out var gov)) e.Gov = gov;
            if (config?.Attribute("Profile")?.Value is { } profile) e.Profile = profile;
            return "<tr2:SetVideoEncoderConfigurationResponse/>";
        }
        var h264 = Child(config, "H264");
        if (Int(h264, "GovLength") is { } g) e.Gov = g;
        if (Text(h264, "H264Profile") is { } p) e.Profile = p;
        return "<trt:SetVideoEncoderConfigurationResponse/>";
    }

    // ── Audio (Media1) ──────────────────────────────────────────────────────────

    public static string AudioConfig(CameraSettings s, string element = "trt:Configuration") => $"""
        <{element} token="AudioEncoder_1">
          <tt:Name>AudioEncoder</tt:Name>
          <tt:UseCount>1</tt:UseCount>
          <tt:Encoding>{s.AudioCodec}</tt:Encoding>
          <tt:Bitrate>{s.AudioKbps}</tt:Bitrate>
          <tt:SampleRate>{s.AudioKhz}</tt:SampleRate>
          <tt:SessionTimeout>PT60S</tt:SessionTimeout>
        </{element}>
        """;

    public static string GetAudioEncoderConfiguration(CameraSettings s)
        => $"<trt:GetAudioEncoderConfigurationResponse>{AudioConfig(s)}</trt:GetAudioEncoderConfigurationResponse>";

    public static string AudioOptions() => """
        <trt:GetAudioEncoderConfigurationOptionsResponse>
          <trt:Options>
            <tt:Options>
              <tt:Encoding>AAC</tt:Encoding>
              <tt:BitrateList><tt:Items>32</tt:Items><tt:Items>48</tt:Items><tt:Items>64</tt:Items></tt:BitrateList>
              <tt:SampleRateList><tt:Items>16</tt:Items><tt:Items>48</tt:Items></tt:SampleRateList>
            </tt:Options>
            <tt:Options>
              <tt:Encoding>G711</tt:Encoding>
              <tt:BitrateList><tt:Items>64</tt:Items></tt:BitrateList>
              <tt:SampleRateList><tt:Items>8</tt:Items></tt:SampleRateList>
            </tt:Options>
          </trt:Options>
        </trt:GetAudioEncoderConfigurationOptionsResponse>
        """;

    public static string SetAudioEncoderConfiguration(CameraSettings s, XElement operation)
    {
        var config = Child(operation, "Configuration");
        if (Text(config, "Encoding") is { } codec) s.AudioCodec = codec;
        if (Int(config, "Bitrate") is { } kbps) s.AudioKbps = kbps;
        if (Int(config, "SampleRate") is { } khz) s.AudioKhz = khz;
        return "<trt:SetAudioEncoderConfigurationResponse/>";
    }

    // ── Imaging ─────────────────────────────────────────────────────────────────

    public static string GetImagingSettings(CameraSettings s) => $"""
        <timg:GetImagingSettingsResponse>
          <timg:ImagingSettings>
            <tt:Brightness>{F(s.Brightness)}</tt:Brightness>
            <tt:ColorSaturation>{F(s.Saturation)}</tt:ColorSaturation>
            <tt:Contrast>{F(s.Contrast)}</tt:Contrast>
            <tt:IrCutFilter>{s.IrCut}</tt:IrCutFilter>
            <tt:Sharpness>{F(s.Sharpness)}</tt:Sharpness>
            <tt:WideDynamicRange><tt:Mode>{s.Wdr}</tt:Mode><tt:Level>{F(s.WdrLevel)}</tt:Level></tt:WideDynamicRange>
            <tt:WhiteBalance><tt:Mode>{s.WhiteBalance}</tt:Mode><tt:CrGain>{F(s.CrGain)}</tt:CrGain><tt:CbGain>{F(s.CbGain)}</tt:CbGain></tt:WhiteBalance>
          </timg:ImagingSettings>
        </timg:GetImagingSettingsResponse>
        """;

    public static string ImagingOptions() => """
        <timg:GetOptionsResponse>
          <timg:ImagingOptions>
            <tt:Brightness><tt:Min>0</tt:Min><tt:Max>100</tt:Max></tt:Brightness>
            <tt:ColorSaturation><tt:Min>0</tt:Min><tt:Max>100</tt:Max></tt:ColorSaturation>
            <tt:Contrast><tt:Min>0</tt:Min><tt:Max>100</tt:Max></tt:Contrast>
            <tt:IrCutFilterModes>ON</tt:IrCutFilterModes>
            <tt:IrCutFilterModes>OFF</tt:IrCutFilterModes>
            <tt:IrCutFilterModes>AUTO</tt:IrCutFilterModes>
            <tt:Sharpness><tt:Min>0</tt:Min><tt:Max>100</tt:Max></tt:Sharpness>
            <tt:WideDynamicRange><tt:Mode>OFF</tt:Mode><tt:Mode>ON</tt:Mode><tt:Level><tt:Min>0</tt:Min><tt:Max>100</tt:Max></tt:Level></tt:WideDynamicRange>
            <tt:WhiteBalance><tt:Mode>AUTO</tt:Mode><tt:Mode>MANUAL</tt:Mode><tt:YrGain><tt:Min>0</tt:Min><tt:Max>255</tt:Max></tt:YrGain><tt:YbGain><tt:Min>0</tt:Min><tt:Max>255</tt:Max></tt:YbGain></tt:WhiteBalance>
          </timg:ImagingOptions>
        </timg:GetOptionsResponse>
        """;

    public static string SetImagingSettings(CameraSettings s, XElement operation)
    {
        var settings = Child(operation, "ImagingSettings");
        if (Double(settings, "Brightness") is { } b) s.Brightness = b;
        if (Double(settings, "ColorSaturation") is { } sat) s.Saturation = sat;
        if (Double(settings, "Contrast") is { } c) s.Contrast = c;
        if (Double(settings, "Sharpness") is { } sh) s.Sharpness = sh;
        if (Text(settings, "IrCutFilter") is { } ir) s.IrCut = ir;
        var wdr = Child(settings, "WideDynamicRange");
        if (Text(wdr, "Mode") is { } wm) s.Wdr = wm;
        if (Double(wdr, "Level") is { } wl) s.WdrLevel = wl;
        var wb = Child(settings, "WhiteBalance");
        if (Text(wb, "Mode") is { } wbm) s.WhiteBalance = wbm;
        if (Double(wb, "CrGain") is { } cr) s.CrGain = cr;
        if (Double(wb, "CbGain") is { } cb) s.CbGain = cb;
        return "<timg:SetImagingSettingsResponse/>";
    }

    // ── XML helpers ─────────────────────────────────────────────────────────────

    private static XElement? Child(XElement? e, string name) => e?.Elements().FirstOrDefault(x => x.Name.LocalName == name);
    private static string? Text(XElement? e, string name) => Child(e, name)?.Value.Trim();
    private static int? Int(XElement? e, string name) => Double(e, name) is { } d ? (int)Math.Round(d) : null;
    private static double? Double(XElement? e, string name)
        => double.TryParse(Text(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}
