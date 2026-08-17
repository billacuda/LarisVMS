using System.Net;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>Multi-sensor (quad-lens etc.) camera support, driven through the real CameraService and
/// the real ONVIF client stack with only the HTTP transport stubbed — the behavior worth proving is
/// that a camera pinned to one VideoSourceToken resolves *only* that lens's streams, which is what
/// stops several Camera rows on one physical device from recording the same lens. A pure unit test
/// against the ranker couldn't show that, since the ranker never sees channels at all.</summary>
public class CameraServiceChannelTests
{
    private const string DeviceUri = "http://10.0.0.5/onvif/device_service";

    /// <summary>Answers the whole probe sequence (GetDeviceInformation, GetCapabilities,
    /// GetServices, GetProfiles) plus one GetStreamUri per profile, dispatching on the SOAP action
    /// in the request body. Each stream URI embeds its profile token so a test can tell which
    /// profile a resolved stream actually came from.</summary>
    private sealed class OnvifStub(int channels, int profilesPerChannel) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var payload = body.Contains("GetDeviceInformation") ? DeviceInformation()
                : body.Contains("GetCapabilities") ? Capabilities()
                : body.Contains("GetServices") ? Services()
                : body.Contains("GetProfiles") ? Profiles()
                : body.Contains("GetStreamUri") ? StreamUri(ProfileTokenFrom(body))
                : "<Empty/>";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Envelope(payload), System.Text.Encoding.UTF8, "text/xml")
            };
        }

        private static string ProfileTokenFrom(string body)
        {
            var open = body.IndexOf("<ProfileToken>", StringComparison.Ordinal);
            if (open < 0) return "unknown";
            var start = open + "<ProfileToken>".Length;
            var end = body.IndexOf("</ProfileToken>", start, StringComparison.Ordinal);
            return end < 0 ? "unknown" : body[start..end];
        }

        private static string Envelope(string inner) => $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <env:Envelope xmlns:env="http://www.w3.org/2003/05/soap-envelope">
              <env:Body xmlns:tds="http://www.onvif.org/ver10/device/wsdl"
                        xmlns:trt="http://www.onvif.org/ver10/media/wsdl"
                        xmlns:tt="http://www.onvif.org/ver10/schema">{inner}</env:Body>
            </env:Envelope>
            """;

        private static string DeviceInformation() => """
            <tds:GetDeviceInformationResponse>
              <tds:Manufacturer>Axis</tds:Manufacturer>
              <tds:Model>Q-Multi-4</tds:Model>
              <tds:FirmwareVersion>1.0</tds:FirmwareVersion>
              <tds:SerialNumber>SN-1</tds:SerialNumber>
            </tds:GetDeviceInformationResponse>
            """;

        private static string Capabilities() => """
            <tds:GetCapabilitiesResponse>
              <tds:Capabilities>
                <tt:Media><tt:XAddr>http://10.0.0.5/onvif/media</tt:XAddr></tt:Media>
              </tds:Capabilities>
            </tds:GetCapabilitiesResponse>
            """;

        private static string Services() => "<tds:GetServicesResponse/>";

        private static string StreamUri(string profileToken) => $"""
            <trt:GetStreamUriResponse>
              <trt:MediaUri><tt:Uri>rtsp://10.0.0.5/{profileToken}</tt:Uri></trt:MediaUri>
            </trt:GetStreamUriResponse>
            """;

        private string Profiles()
        {
            var profiles = string.Concat(
                from channel in Enumerable.Range(1, channels)
                from index in Enumerable.Range(1, profilesPerChannel)
                let isMain = index == 1
                select $"""
                    <trt:Profiles token="ch{channel}_p{index}">
                      <tt:Name>Channel{channel}_{(isMain ? "MainStream" : $"SubStream{index - 1}")}</tt:Name>
                      <tt:VideoSourceConfiguration token="vsc_{channel}">
                        <tt:SourceToken>VideoSource_{channel}</tt:SourceToken>
                      </tt:VideoSourceConfiguration>
                      <tt:VideoEncoderConfiguration>
                        <tt:Encoding>H264</tt:Encoding>
                        <tt:Resolution><tt:Width>{(isMain ? 2560 : 704)}</tt:Width><tt:Height>{(isMain ? 1440 : 480)}</tt:Height></tt:Resolution>
                      </tt:VideoEncoderConfiguration>
                    </trt:Profiles>
                    """);
            return $"<trt:GetProfilesResponse>{profiles}</trt:GetProfilesResponse>";
        }
    }

    private static (ApplicationDbContext Db, CameraService Service) NewService(int channels, int profilesPerChannel = 2)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new ApplicationDbContext(options);
        return (db, new CameraService(db, () => new HttpClient(new OnvifStub(channels, profilesPerChannel))));
    }

    private static async Task<Camera> SeedCameraAsync(ApplicationDbContext db, string? videoSourceToken = null)
    {
        var camera = new Camera
        {
            Id = Guid.NewGuid(),
            Name = "Front Door",
            Host = "10.0.0.5",
            DeviceServiceUri = DeviceUri,
            VideoSourceToken = videoSourceToken
        };
        db.Cameras.Add(camera);
        await db.SaveChangesAsync();
        return camera;
    }

    [Fact]
    public async Task ProbeReportsEveryChannelTheDeviceExposes()
    {
        var (db, service) = NewService(channels: 4);
        var camera = await SeedCameraAsync(db);

        var summary = await service.ProbeAsync(camera.Id);

        Assert.Null(summary.Error);
        Assert.Equal(
            ["VideoSource_1", "VideoSource_2", "VideoSource_3", "VideoSource_4"],
            summary.VideoSourceTokens);
    }

    [Fact]
    public async Task AChannelPinnedCameraResolvesOnlyItsOwnLensStreams()
    {
        var (db, service) = NewService(channels: 4);
        var camera = await SeedCameraAsync(db, videoSourceToken: "VideoSource_3");

        await service.ProbeAsync(camera.Id);

        var streams = await db.CameraStreams.Where(s => s.CameraId == camera.Id).ToListAsync();
        Assert.NotEmpty(streams);
        // Every resolved stream must come from channel 3's own profiles — the stub encodes the
        // profile token into the RTSP URI precisely so this is checkable.
        Assert.All(streams, s => Assert.StartsWith("rtsp://10.0.0.5/ch3_", s.RtspUri));
        Assert.Contains(streams, s => s.Role == CameraStreamRole.Main);
    }

    [Fact]
    public async Task AnUnpinnedCameraKeepsTheOriginalAcrossAllProfilesBehavior()
    {
        // Backward compatibility: a single-sensor camera, and every camera added before multi-channel
        // support existed, has no token and must behave exactly as it always did.
        var (db, service) = NewService(channels: 1, profilesPerChannel: 3);
        var camera = await SeedCameraAsync(db);

        var summary = await service.ProbeAsync(camera.Id);

        Assert.Equal(3, summary.StreamCount);
        var streams = await db.CameraStreams.Where(s => s.CameraId == camera.Id).ToListAsync();
        Assert.Equal(3, streams.Count);
    }

    [Fact]
    public async Task SplittingCreatesOneCameraPerRemainingChannelAndPinsTheOriginalToTheFirst()
    {
        var (db, service) = NewService(channels: 4);
        var camera = await SeedCameraAsync(db);

        var created = await service.SplitChannelsAsync(camera.Id);

        Assert.Equal(3, created); // the original claims channel 1, three siblings are added
        var cameras = await db.Cameras.OrderBy(c => c.Name).ToListAsync();
        Assert.Equal(4, cameras.Count);
        Assert.Equal(
            ["VideoSource_1", "VideoSource_2", "VideoSource_3", "VideoSource_4"],
            cameras.Select(c => c.VideoSourceToken).Order());
        Assert.Equal(
            ["Front Door — Ch1", "Front Door — Ch2", "Front Door — Ch3", "Front Door — Ch4"],
            cameras.Select(c => c.Name));
        // Siblings inherit the device identity so each can be probed/recorded on its own.
        Assert.All(cameras, c => Assert.Equal(DeviceUri, c.DeviceServiceUri));
    }

    [Fact]
    public async Task SplittingTwiceAddsNothingTheSecondTime()
    {
        // Re-probing/re-splitting a device must match existing rows by channel rather than
        // duplicating them — the identity-matching requirement for this feature.
        var (db, service) = NewService(channels: 4);
        var camera = await SeedCameraAsync(db);

        Assert.Equal(3, await service.SplitChannelsAsync(camera.Id));
        Assert.Equal(0, await service.SplitChannelsAsync(camera.Id));
        Assert.Equal(4, await db.Cameras.CountAsync());
        // And the name must not accumulate suffixes on repeat runs.
        Assert.Contains(await db.Cameras.ToListAsync(), c => c.Name == "Front Door — Ch1");
    }

    [Fact]
    public async Task SplittingASingleSensorDeviceDoesNothing()
    {
        var (db, service) = NewService(channels: 1);
        var camera = await SeedCameraAsync(db);

        var created = await service.SplitChannelsAsync(camera.Id);

        Assert.Equal(0, created);
        Assert.Equal(1, await db.Cameras.CountAsync());
        // Left unpinned, so it keeps ranking across every profile exactly as before.
        Assert.Null((await db.Cameras.FirstAsync()).VideoSourceToken);
    }
}
