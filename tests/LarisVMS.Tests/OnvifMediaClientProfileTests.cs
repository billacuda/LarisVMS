using System.Net;
using LarisVMS.Onvif.Clients;
using LarisVMS.Onvif.Soap;

namespace LarisVMS.Tests;

/// <summary>Parses real-shaped GetProfiles responses through the actual OnvifMediaClient (stubbing
/// only the HTTP transport), rather than asserting against a hand-built OnvifMediaProfile — the
/// thing worth pinning here is the XML reading itself, especially VideoSourceToken, which is the
/// only field in a GetProfiles response that distinguishes one lens of a multi-sensor device from
/// another.</summary>
public class OnvifMediaClientProfileTests
{
    private sealed class StubHandler(string responseXml) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseXml, System.Text.Encoding.UTF8, "text/xml")
            });
    }

    private static Task<IReadOnlyList<OnvifMediaProfile>> GetProfilesAsync(string responseXml)
    {
        var client = new OnvifMediaClient(new OnvifSoapClient(new HttpClient(new StubHandler(responseXml))));
        return client.GetProfilesAsync(new Uri("http://camera.example/onvif/media"), null);
    }

    /// <summary>SOAP 1.2 envelope on the response, matching what real devices actually reply with
    /// regardless of the request's SOAP version (see OnvifSoapClient's own doc comment).</summary>
    private static string Envelope(string profilesXml) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <env:Envelope xmlns:env="http://www.w3.org/2003/05/soap-envelope">
          <env:Body>
            <trt:GetProfilesResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl"
                                     xmlns:tt="http://www.onvif.org/ver10/schema">
              {profilesXml}
            </trt:GetProfilesResponse>
          </env:Body>
        </env:Envelope>
        """;

    private static string Profile(string token, string name, string? sourceToken, int width, int height) => $"""
        <trt:Profiles token="{token}" fixed="true">
          <tt:Name>{name}</tt:Name>
          {(sourceToken is null ? "" : $"""
          <tt:VideoSourceConfiguration token="VideoSourceConfig_{sourceToken}">
            <tt:Name>VideoSourceConfig</tt:Name>
            <tt:UseCount>2</tt:UseCount>
            <tt:SourceToken>{sourceToken}</tt:SourceToken>
            <tt:Bounds x="0" y="0" width="{width}" height="{height}"/>
          </tt:VideoSourceConfiguration>
          """)}
          <tt:VideoEncoderConfiguration token="VideoEncoder_{token}">
            <tt:Name>VideoEncoder</tt:Name>
            <tt:UseCount>1</tt:UseCount>
            <tt:Encoding>H264</tt:Encoding>
            <tt:Resolution><tt:Width>{width}</tt:Width><tt:Height>{height}</tt:Height></tt:Resolution>
            <tt:RateControl><tt:FrameRateLimit>20</tt:FrameRateLimit><tt:BitrateLimit>4096</tt:BitrateLimit></tt:RateControl>
          </tt:VideoEncoderConfiguration>
        </trt:Profiles>
        """;

    [Fact]
    public async Task ReadsTheVideoSourceTokenOfEachProfile()
    {
        var xml = Envelope(
            Profile("Profile_1", "MainStream", "VideoSource_1", 2560, 1440) +
            Profile("Profile_2", "SubStream", "VideoSource_1", 704, 480));

        var profiles = await GetProfilesAsync(xml);

        Assert.Equal(2, profiles.Count);
        Assert.All(profiles, p => Assert.Equal("VideoSource_1", p.VideoSourceToken));
        // The rest of the parsing must keep working unchanged alongside the new field.
        Assert.Equal("MainStream", profiles[0].Name);
        Assert.Equal(2560, profiles[0].Width);
        Assert.Equal("H264", profiles[0].VideoEncoding);
    }

    [Fact]
    public async Task DistinguishesEachLensOfAMultiSensorDevice()
    {
        // A quad-lens device: four sensors, each exposing a main and a sub profile.
        var profilesXml = string.Concat(Enumerable.Range(1, 4).Select(channel =>
            Profile($"Profile_{channel}_main", $"Channel{channel}_MainStream", $"VideoSource_{channel}", 2560, 1440) +
            Profile($"Profile_{channel}_sub", $"Channel{channel}_SubStream", $"VideoSource_{channel}", 704, 480)));

        var profiles = await GetProfilesAsync(Envelope(profilesXml));

        Assert.Equal(8, profiles.Count);

        // The grouping SplitChannelsAsync/ReplaceStreamsAsync rely on: four distinct sensors, and
        // each one's own profiles resolvable independently of the others.
        var channels = profiles.Select(p => p.VideoSourceToken).Distinct().ToList();
        Assert.Equal(["VideoSource_1", "VideoSource_2", "VideoSource_3", "VideoSource_4"], channels);
        Assert.All(channels, channel =>
            Assert.Equal(2, profiles.Count(p => p.VideoSourceToken == channel)));
    }

    [Fact]
    public async Task LeavesTheTokenNullWhenTheProfileHasNoVideoSourceConfiguration()
    {
        // Firmware that omits VideoSourceConfiguration must degrade to the ordinary single-channel
        // behavior (null token = "use every profile"), not drop the profile entirely.
        var xml = Envelope(Profile("Profile_1", "MainStream", sourceToken: null, 2560, 1440));

        var profiles = await GetProfilesAsync(xml);

        Assert.Single(profiles);
        Assert.Null(profiles[0].VideoSourceToken);
        Assert.Equal("MainStream", profiles[0].Name);
    }
}
