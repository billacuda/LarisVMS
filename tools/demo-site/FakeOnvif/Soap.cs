using System.Security;
using System.Xml;

namespace FakeOnvif;

/// <summary>SOAP 1.2 envelopes and the response bodies LarisVMS's ONVIF clients read
/// (src/LarisVMS.Onvif/Clients). Those parsers match elements by local name, but the namespaces here
/// follow the ONVIF WSDLs anyway so the responses look like a real camera's.</summary>
public static class Soap
{
    public const string ContentType = "application/soap+xml; charset=utf-8";

    public static string Envelope(string body) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"
                    xmlns:tt="http://www.onvif.org/ver10/schema"
                    xmlns:tds="http://www.onvif.org/ver10/device/wsdl"
                    xmlns:trt="http://www.onvif.org/ver10/media/wsdl"
                    xmlns:tr2="http://www.onvif.org/ver20/media/wsdl"
                    xmlns:timg="http://www.onvif.org/ver20/imaging/wsdl"
                    xmlns:tev="http://www.onvif.org/ver10/events/wsdl"
                    xmlns:wsnt="http://docs.oasis-open.org/wsn/b-2"
                    xmlns:wsa="http://www.w3.org/2005/08/addressing"
                    xmlns:tns1="http://www.onvif.org/ver10/topics"
                    xmlns:ter="http://www.onvif.org/ver10/error">
          <s:Body>
        {body}
          </s:Body>
        </s:Envelope>
        """;

    public static string Fault(string subcode, string reason) => Envelope($"""
            <s:Fault>
              <s:Code><s:Value>s:Sender</s:Value><s:Subcode><s:Value>ter:{subcode}</s:Value></s:Subcode></s:Code>
              <s:Reason><s:Text xml:lang="en">{Esc(reason)}</s:Text></s:Reason>
            </s:Fault>
        """);

    public static string Esc(string value) => SecurityElement.Escape(value);

    public static string Time(DateTime utc) => XmlConvert.ToString(utc, XmlDateTimeSerializationMode.Utc);

    // ── Device service ──────────────────────────────────────────────────────────

    public static string DeviceInformation(CameraConfig c) => $"""
        <tds:GetDeviceInformationResponse>
          <tds:Manufacturer>{CameraConfig.Manufacturer}</tds:Manufacturer>
          <tds:Model>{Esc(c.Model)}</tds:Model>
          <tds:FirmwareVersion>{Esc(c.Firmware)}</tds:FirmwareVersion>
          <tds:SerialNumber>{c.SerialNumber}</tds:SerialNumber>
          <tds:HardwareId>{Esc(c.HardwareId)}</tds:HardwareId>
        </tds:GetDeviceInformationResponse>
        """;

    public static string Capabilities(CameraConfig c) => $"""
        <tds:GetCapabilitiesResponse>
          <tds:Capabilities>
            <tt:Device><tt:XAddr>{c.DeviceServiceUrl}</tt:XAddr></tt:Device>
            <tt:Events>
              <tt:XAddr>{c.EventServiceUrl}</tt:XAddr>
              <tt:WSSubscriptionPolicySupport>false</tt:WSSubscriptionPolicySupport>
              <tt:WSPullPointSupport>true</tt:WSPullPointSupport>
              <tt:WSPausableSubscriptionManagerInterfaceSupport>false</tt:WSPausableSubscriptionManagerInterfaceSupport>
            </tt:Events>
            <tt:Media>
              <tt:XAddr>{c.MediaServiceUrl}</tt:XAddr>
              <tt:StreamingCapabilities>
                <tt:RTPMulticast>false</tt:RTPMulticast>
                <tt:RTP_TCP>true</tt:RTP_TCP>
                <tt:RTP_RTSP_TCP>true</tt:RTP_RTSP_TCP>
              </tt:StreamingCapabilities>
            </tt:Media>
            <tt:Imaging><tt:XAddr>{c.ImagingServiceUrl}</tt:XAddr></tt:Imaging>
          </tds:Capabilities>
        </tds:GetCapabilitiesResponse>
        """;

    public static string Services(CameraConfig c) => $"""
        <tds:GetServicesResponse>
        {Service("http://www.onvif.org/ver10/device/wsdl", c.DeviceServiceUrl)}
        {Service("http://www.onvif.org/ver10/media/wsdl", c.MediaServiceUrl)}
        {(c.Codec == "H265" ? Service("http://www.onvif.org/ver20/media/wsdl", c.Media2ServiceUrl) : "")}
        {Service("http://www.onvif.org/ver20/imaging/wsdl", c.ImagingServiceUrl)}
        {Service("http://www.onvif.org/ver10/events/wsdl", c.EventServiceUrl)}
        </tds:GetServicesResponse>
        """;

    private static string Service(string ns, string xAddr) => $"""
          <tds:Service>
            <tds:Namespace>{ns}</tds:Namespace>
            <tds:XAddr>{xAddr}</tds:XAddr>
            <tds:Version><tt:Major>2</tt:Major><tt:Minor>60</tt:Minor></tds:Version>
          </tds:Service>
        """;

    public static string SystemDateAndTime()
    {
        var now = DateTime.UtcNow;
        return $"""
            <tds:GetSystemDateAndTimeResponse>
              <tds:SystemDateAndTime>
                <tt:DateTimeType>NTP</tt:DateTimeType>
                <tt:DaylightSavings>false</tt:DaylightSavings>
                <tt:TimeZone><tt:TZ>UTC0</tt:TZ></tt:TimeZone>
                <tt:UTCDateTime>
                  <tt:Time><tt:Hour>{now.Hour}</tt:Hour><tt:Minute>{now.Minute}</tt:Minute><tt:Second>{now.Second}</tt:Second></tt:Time>
                  <tt:Date><tt:Year>{now.Year}</tt:Year><tt:Month>{now.Month}</tt:Month><tt:Day>{now.Day}</tt:Day></tt:Date>
                </tt:UTCDateTime>
              </tds:SystemDateAndTime>
            </tds:GetSystemDateAndTimeResponse>
            """;
    }

    public static string Scopes(CameraConfig c) => $"""
        <tds:GetScopesResponse>
          <tds:Scopes><tt:ScopeDef>Fixed</tt:ScopeDef><tt:ScopeItem>onvif://www.onvif.org/type/video_encoder</tt:ScopeItem></tds:Scopes>
          <tds:Scopes><tt:ScopeDef>Fixed</tt:ScopeDef><tt:ScopeItem>onvif://www.onvif.org/Profile/Streaming</tt:ScopeItem></tds:Scopes>
          <tds:Scopes><tt:ScopeDef>Fixed</tt:ScopeDef><tt:ScopeItem>onvif://www.onvif.org/hardware/{Uri.EscapeDataString(c.Model)}</tt:ScopeItem></tds:Scopes>
          <tds:Scopes><tt:ScopeDef>Configurable</tt:ScopeDef><tt:ScopeItem>onvif://www.onvif.org/name/{Uri.EscapeDataString(c.Name)}</tt:ScopeItem></tds:Scopes>
        </tds:GetScopesResponse>
        """;

    // ── Media service ───────────────────────────────────────────────────────────

    public const string MainToken = "Profile_1";
    public const string SubToken = "Profile_2";

    /// <summary>Encoder and audio values come from DeviceSettingsStore, so a change made through
    /// Device settings shows up in the next probe.</summary>
    public static string Profiles(CameraConfig c, CameraSettings s) => $"""
        <trt:GetProfilesResponse>
        {Profile(MainToken, s.Encoders[MainToken], c.Audio ? s : null)}
        {Profile(SubToken, s.Encoders[SubToken], null)}
        </trt:GetProfilesResponse>
        """;

    private static string Profile(string token, EncoderSettings e, CameraSettings? audio) => $"""
          <trt:Profiles token="{token}" fixed="true">
            <tt:Name>{e.Name}</tt:Name>
            <tt:VideoSourceConfiguration token="VideoSourceConfig_1">
              <tt:Name>VideoSourceConfig</tt:Name>
              <tt:UseCount>2</tt:UseCount>
              <tt:SourceToken>VideoSource_1</tt:SourceToken>
              <tt:Bounds x="0" y="0" width="1920" height="1080"/>
            </tt:VideoSourceConfiguration>
        {DeviceSettingsSoap.Media1Config(e, "tt:VideoEncoderConfiguration")}
        {(audio is not null ? $"""
            <tt:AudioSourceConfiguration token="AudioSourceConfig_1">
              <tt:Name>AudioSourceConfig</tt:Name>
              <tt:UseCount>1</tt:UseCount>
              <tt:SourceToken>AudioSource_1</tt:SourceToken>
            </tt:AudioSourceConfiguration>
        {DeviceSettingsSoap.AudioConfig(audio, "tt:AudioEncoderConfiguration")}
        """ : "")}
          </trt:Profiles>
        """;

    public static string StreamUri(CameraConfig c, int rtspPort, string? profileToken)
    {
        var stream = profileToken == SubToken ? "sub" : "main";
        return MediaUri("GetStreamUriResponse", $"rtsp://{c.Ip}:{rtspPort}/{c.Id}_{stream}");
    }

    public static string SnapshotUri(CameraConfig c) => MediaUri("GetSnapshotUriResponse", c.SnapshotUrl);

    private static string MediaUri(string element, string uri) => $"""
        <trt:{element}>
          <trt:MediaUri>
            <tt:Uri>{Esc(uri)}</tt:Uri>
            <tt:InvalidAfterConnect>false</tt:InvalidAfterConnect>
            <tt:InvalidAfterReboot>false</tt:InvalidAfterReboot>
            <tt:Timeout>PT0S</tt:Timeout>
          </trt:MediaUri>
        </trt:{element}>
        """;

    public static string VideoSources() => """
        <trt:GetVideoSourcesResponse>
          <trt:VideoSources token="VideoSource_1">
            <tt:Framerate>25</tt:Framerate>
            <tt:Resolution><tt:Width>1920</tt:Width><tt:Height>1080</tt:Height></tt:Resolution>
          </trt:VideoSources>
        </trt:GetVideoSourcesResponse>
        """;
}
