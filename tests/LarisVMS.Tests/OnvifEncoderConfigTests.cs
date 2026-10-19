using System.Net;
using System.Xml.Linq;
using LarisVMS.Onvif.Clients;
using LarisVMS.Onvif.Soap;

namespace LarisVMS.Tests;

/// <summary>Media1 and Media2 encoder configuration parsing and the Set request bodies, through the
/// real clients with only the HTTP transport stubbed. The thing worth pinning is that a Set sends the
/// configuration back with everything it doesn't model (Multicast, SessionTimeout, vendor extensions)
/// untouched, and new elements land in schema order.</summary>
public class OnvifEncoderConfigTests
{
    private const string Tt = "http://www.onvif.org/ver10/schema";

    private sealed class CapturingHandler(string responseXml) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseXml, System.Text.Encoding.UTF8, "text/xml") };
        }
    }

    private static string Envelope(string body) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <env:Envelope xmlns:env="http://www.w3.org/2003/05/soap-envelope"
                      xmlns:trt="http://www.onvif.org/ver10/media/wsdl"
                      xmlns:tr2="http://www.onvif.org/ver20/media/wsdl"
                      xmlns:tt="{Tt}"
                      xmlns:vnd="http://vendor.example/ext">
          <env:Body>{body}</env:Body>
        </env:Envelope>
        """;

    private const string Media1Config = """
        <trt:Configuration token="VE_1">
          <tt:Name>Main</tt:Name>
          <tt:UseCount>1</tt:UseCount>
          <tt:Encoding>H264</tt:Encoding>
          <tt:Resolution><tt:Width>1920</tt:Width><tt:Height>1080</tt:Height></tt:Resolution>
          <tt:Quality>4</tt:Quality>
          <tt:RateControl><tt:FrameRateLimit>25</tt:FrameRateLimit><tt:EncodingInterval>1</tt:EncodingInterval><tt:BitrateLimit>4096</tt:BitrateLimit></tt:RateControl>
          <tt:H264><tt:GovLength>50</tt:GovLength><tt:H264Profile>Main</tt:H264Profile></tt:H264>
          <tt:Multicast><tt:Port>1234</tt:Port></tt:Multicast>
          <tt:SessionTimeout>PT60S</tt:SessionTimeout>
          <vnd:SmartCodec>on</vnd:SmartCodec>
        </trt:Configuration>
        """;

    private static (OnvifMediaClient Client, CapturingHandler Handler) Media1(string responseBody)
    {
        var handler = new CapturingHandler(Envelope(responseBody));
        return (new OnvifMediaClient(new OnvifSoapClient(new HttpClient(handler))), handler);
    }

    private static readonly Uri MediaUri = new("http://camera.example/onvif/media");

    [Fact]
    public async Task Media1ConfigurationParses()
    {
        var (client, _) = Media1($"<trt:GetVideoEncoderConfigurationResponse>{Media1Config}</trt:GetVideoEncoderConfigurationResponse>");

        var config = await client.GetVideoEncoderConfigurationAsync(MediaUri, "VE_1", null);

        Assert.Equal("VE_1", config.Token);
        Assert.Equal("H264", config.Encoding);
        Assert.Equal(new OnvifResolution(1920, 1080), config.Resolution);
        Assert.Equal(25, config.FrameRateLimit);
        Assert.Equal(4096, config.BitrateLimitKbps);
        Assert.Equal(50, config.GovLength);
        Assert.Equal("Main", config.Profile);
        Assert.Null(config.ConstantBitRate); // Media1 has no standard CBR/VBR field
    }

    [Fact]
    public async Task Media1OptionsReadBitrateFromTheExtension()
    {
        var (client, _) = Media1("""
            <trt:GetVideoEncoderConfigurationOptionsResponse>
              <trt:Options>
                <tt:QualityRange><tt:Min>1</tt:Min><tt:Max>6</tt:Max></tt:QualityRange>
                <tt:H264>
                  <tt:ResolutionsAvailable><tt:Width>1920</tt:Width><tt:Height>1080</tt:Height></tt:ResolutionsAvailable>
                  <tt:ResolutionsAvailable><tt:Width>1280</tt:Width><tt:Height>720</tt:Height></tt:ResolutionsAvailable>
                  <tt:GovLengthRange><tt:Min>1</tt:Min><tt:Max>150</tt:Max></tt:GovLengthRange>
                  <tt:FrameRateRange><tt:Min>1</tt:Min><tt:Max>30</tt:Max></tt:FrameRateRange>
                  <tt:H264ProfilesSupported>Main</tt:H264ProfilesSupported>
                  <tt:H264ProfilesSupported>High</tt:H264ProfilesSupported>
                </tt:H264>
                <tt:Extension><tt:H264><tt:BitrateRange><tt:Min>64</tt:Min><tt:Max>8192</tt:Max></tt:BitrateRange></tt:H264></tt:Extension>
              </trt:Options>
            </trt:GetVideoEncoderConfigurationOptionsResponse>
            """);

        var options = await client.GetVideoEncoderConfigurationOptionsAsync(MediaUri, "VE_1", "Profile_1", null);

        var h264 = Assert.Single(options);
        Assert.Equal("H264", h264.Encoding);
        Assert.Equal(2, h264.Resolutions.Count);
        Assert.Equal(new OnvifRange(64, 8192), h264.BitrateRangeKbps);
        Assert.Equal(new OnvifRange(1, 30), h264.FrameRateRange);
        Assert.Empty(h264.FrameRatesSupported);
        Assert.Equal(new[] { "Main", "High" }, h264.Profiles);
        Assert.False(h264.ConstantBitRateSupported);
    }

    [Fact]
    public void Media1SetKeepsWhatItDoesNotModelAndPatchesTheRest()
    {
        var current = OnvifMediaClient.ParseVideoConfig(XElement.Parse(Envelope(Media1Config)).Descendants().First(e => e.Name.LocalName == "Configuration"));

        var config = OnvifMediaClient.BuildVideoConfigForSet(current,
            new OnvifVideoEncoderChange(Resolution: new OnvifResolution(1280, 720), FrameRateLimit: 15, GovLength: 30));

        Assert.Equal("1280", config.Descendants().First(e => e.Name.LocalName == "Width").Value);
        Assert.Equal("15", config.Descendants().First(e => e.Name.LocalName == "FrameRateLimit").Value);
        Assert.Equal("30", config.Descendants().First(e => e.Name.LocalName == "GovLength").Value);
        Assert.Equal("4096", config.Descendants().First(e => e.Name.LocalName == "BitrateLimit").Value);
        Assert.Equal("1234", config.Descendants().First(e => e.Name.LocalName == "Port").Value);
        Assert.Equal("on", config.Elements().First(e => e.Name.LocalName == "SmartCodec").Value);
        Assert.Equal("VE_1", config.Attribute("token")?.Value);
    }

    [Fact]
    public void Media1SwitchingFromJpegAddsTheH264BlockInSchemaOrder()
    {
        var jpeg = XElement.Parse($"""
            <Configuration token="VE_2" xmlns:tt="{Tt}">
              <tt:Name>Sub</tt:Name><tt:UseCount>1</tt:UseCount><tt:Encoding>JPEG</tt:Encoding>
              <tt:Resolution><tt:Width>640</tt:Width><tt:Height>360</tt:Height></tt:Resolution>
              <tt:Quality>4</tt:Quality>
              <tt:RateControl><tt:FrameRateLimit>5</tt:FrameRateLimit><tt:BitrateLimit>512</tt:BitrateLimit></tt:RateControl>
              <tt:Multicast/><tt:SessionTimeout>PT60S</tt:SessionTimeout>
            </Configuration>
            """);
        var current = OnvifMediaClient.ParseVideoConfig(jpeg);

        var config = OnvifMediaClient.BuildVideoConfigForSet(current, new OnvifVideoEncoderChange(Encoding: "H264"));

        var names = config.Elements().Select(e => e.Name.LocalName).ToList();
        Assert.True(names.IndexOf("H264") > names.IndexOf("RateControl"));
        Assert.True(names.IndexOf("H264") < names.IndexOf("Multicast"));
        var h264 = config.Elements().First(e => e.Name.LocalName == "H264");
        Assert.Equal(Tt, h264.Name.NamespaceName);
        Assert.Equal(new[] { "GovLength", "H264Profile" }, h264.Elements().Select(e => e.Name.LocalName));
    }

    [Fact]
    public async Task Media1SetSendsForcePersistence()
    {
        var (client, handler) = Media1("<trt:SetVideoEncoderConfigurationResponse/>");
        var current = OnvifMediaClient.ParseVideoConfig(XElement.Parse(Envelope(Media1Config)).Descendants().First(e => e.Name.LocalName == "Configuration"));

        await client.SetVideoEncoderConfigurationAsync(MediaUri, current, new OnvifVideoEncoderChange(BitrateLimitKbps: 2048), null);

        Assert.Contains("<ForcePersistence>true</ForcePersistence>", handler.LastRequestBody);
        Assert.Contains("2048", handler.LastRequestBody);
    }

    // ── Media2 ──────────────────────────────────────────────────────────────

    private static string Media2Options(string cbr) => $"""
        <tr2:GetVideoEncoderConfigurationOptionsResponse>
          <tr2:Options GovLengthRange="1 150" FrameRatesSupported="25 20 12.5" ProfilesSupported="Main High" {cbr}>
            <tt:Encoding>H264</tt:Encoding>
            <tt:QualityRange><tt:Min>1</tt:Min><tt:Max>6</tt:Max></tt:QualityRange>
            <tt:ResolutionsAvailable><tt:Width>2560</tt:Width><tt:Height>1440</tt:Height></tt:ResolutionsAvailable>
            <tt:BitrateRange><tt:Min>32</tt:Min><tt:Max>8192</tt:Max></tt:BitrateRange>
          </tr2:Options>
          <tr2:Options GovLengthRange="1 150" FrameRatesSupported="25 20" ProfilesSupported="Main" {cbr}>
            <tt:Encoding>H265</tt:Encoding>
            <tt:ResolutionsAvailable><tt:Width>2560</tt:Width><tt:Height>1440</tt:Height></tt:ResolutionsAvailable>
            <tt:BitrateRange><tt:Min>32</tt:Min><tt:Max>6144</tt:Max></tt:BitrateRange>
          </tr2:Options>
        </tr2:GetVideoEncoderConfigurationOptionsResponse>
        """;

    [Fact]
    public void Media2OptionsReadH265FrameRateListsAndCbrSupport()
    {
        var options = OnvifMedia2Client.ParseOptions(XElement.Parse(Envelope(Media2Options("ConstantBitRateSupported=\"true\"")))
            .Descendants().First(e => e.Name.LocalName == "GetVideoEncoderConfigurationOptionsResponse"));

        Assert.Equal(new[] { "H264", "H265" }, options.Select(o => o.Encoding));
        var h264 = options[0];
        Assert.Equal(new[] { 25, 20, 12.5 }, h264.FrameRatesSupported);
        Assert.Equal(new OnvifRange(1, 150), h264.GovLengthRange);
        Assert.Equal(new[] { "Main", "High" }, h264.Profiles);
        Assert.True(h264.ConstantBitRateSupported);
        Assert.Null(options[1].QualityRange);
    }

    [Fact]
    public void Media2WithoutTheCbrFlagDoesNotOfferIt()
    {
        var options = OnvifMedia2Client.ParseOptions(XElement.Parse(Envelope(Media2Options("")))
            .Descendants().First(e => e.Name.LocalName == "GetVideoEncoderConfigurationOptionsResponse"));

        Assert.All(options, o => Assert.False(o.ConstantBitRateSupported));
    }

    [Fact]
    public void Media2ConfigReadsAttributesAndSetPatchesThem()
    {
        var current = OnvifMedia2Client.ParseConfig(XElement.Parse($"""
            <Configurations token="VE_1" GovLength="50" Profile="High" xmlns:tt="{Tt}">
              <tt:Name>Main</tt:Name><tt:UseCount>2</tt:UseCount><tt:Encoding>H264</tt:Encoding>
              <tt:Resolution><tt:Width>2560</tt:Width><tt:Height>1440</tt:Height></tt:Resolution>
              <tt:RateControl ConstantBitRate="false"><tt:FrameRateLimit>25</tt:FrameRateLimit><tt:BitrateLimit>4096</tt:BitrateLimit></tt:RateControl>
              <tt:Multicast/>
              <tt:Quality>4</tt:Quality>
            </Configurations>
            """));

        Assert.False(current.ConstantBitRate);
        Assert.Equal(50, current.GovLength);
        Assert.Equal("High", current.Profile);
        Assert.Equal(2, current.UseCount);

        var config = OnvifMedia2Client.BuildConfigForSet(current,
            new OnvifVideoEncoderChange(Encoding: "H265", ConstantBitRate: true, FrameRateLimit: 12.5, GovLength: 25));

        Assert.Equal("H265", config.Elements().First(e => e.Name.LocalName == "Encoding").Value);
        var rate = config.Elements().First(e => e.Name.LocalName == "RateControl");
        Assert.Equal("true", rate.Attribute("ConstantBitRate")?.Value);
        Assert.Equal("12.5", rate.Elements().First(e => e.Name.LocalName == "FrameRateLimit").Value);
        Assert.Equal("25", config.Attribute("GovLength")?.Value);
        // An H.264 profile name means nothing to H.265, so switching codec without a new one drops it.
        Assert.Null(config.Attribute("Profile"));
    }

    // ── Imaging ─────────────────────────────────────────────────────────────

    [Fact]
    public void ImagingOptionsMapTheGainRangesAndModes()
    {
        var options = OnvifImagingClient.ParseOptions(XElement.Parse($"""
            <ImagingOptions xmlns:tt="{Tt}">
              <tt:Brightness><tt:Min>0</tt:Min><tt:Max>100</tt:Max></tt:Brightness>
              <tt:IrCutFilterModes>ON</tt:IrCutFilterModes><tt:IrCutFilterModes>OFF</tt:IrCutFilterModes><tt:IrCutFilterModes>AUTO</tt:IrCutFilterModes>
              <tt:WideDynamicRange><tt:Mode>OFF</tt:Mode><tt:Mode>ON</tt:Mode><tt:Level><tt:Min>0</tt:Min><tt:Max>100</tt:Max></tt:Level></tt:WideDynamicRange>
              <tt:WhiteBalance><tt:Mode>AUTO</tt:Mode><tt:Mode>MANUAL</tt:Mode><tt:YrGain><tt:Min>0</tt:Min><tt:Max>255</tt:Max></tt:YrGain><tt:YbGain><tt:Min>0</tt:Min><tt:Max>255</tt:Max></tt:YbGain></tt:WhiteBalance>
            </ImagingOptions>
            """));

        Assert.Equal(new OnvifRange(0, 100), options.Brightness);
        Assert.Null(options.Contrast);
        Assert.Equal(new[] { "ON", "OFF", "AUTO" }, options.IrCutFilterModes);
        Assert.Equal(new[] { "OFF", "ON" }, options.WideDynamicRangeModes);
        Assert.Empty(options.BacklightCompensationModes);
        Assert.Equal(new OnvifRange(0, 255), options.WhiteBalanceCrGain);
        Assert.Equal(new OnvifRange(0, 255), options.WhiteBalanceCbGain);
    }

    [Fact]
    public void ImagingSetSendsOnlyTheChangedSettingsWholeAndInOrder()
    {
        var current = OnvifImagingClient.ParseSettings(XElement.Parse($"""
            <ImagingSettings xmlns:tt="{Tt}">
              <tt:Brightness>50</tt:Brightness>
              <tt:Exposure><tt:Mode>AUTO</tt:Mode><tt:MinExposureTime>10</tt:MinExposureTime></tt:Exposure>
              <tt:Focus><tt:AutoFocusMode>AUTO</tt:AutoFocusMode></tt:Focus>
              <tt:WideDynamicRange><tt:Mode>OFF</tt:Mode><tt:Level>40</tt:Level></tt:WideDynamicRange>
            </ImagingSettings>
            """));

        var settings = OnvifImagingClient.BuildSettingsForSet(current,
            new OnvifImagingChange(WideDynamicRangeMode: "ON", Brightness: 70));

        Assert.Equal(new[] { "Brightness", "WideDynamicRange" }, settings.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("70", settings.Elements().First().Value);
        var wdr = settings.Elements().Last();
        Assert.Equal("ON", wdr.Elements().First(e => e.Name.LocalName == "Mode").Value);
        Assert.Equal("40", wdr.Elements().First(e => e.Name.LocalName == "Level").Value); // kept from current
    }
}
