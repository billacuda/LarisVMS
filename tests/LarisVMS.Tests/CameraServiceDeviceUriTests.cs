using System.Net;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>
/// Cameras/Edit's device service URL field was read-only after creation — no code path even accepted
/// a new value, which meant switching a camera between http and https (or following an IP change)
/// required deleting and re-adding it. UpdateAsync now accepts the field the same way it already
/// accepts credentials: blank means "leave unchanged," a real value is validated, applied, and
/// re-derives Host/OnvifPort exactly as AddAsync does so Cameras/Index can't show a stale address.
/// </summary>
public class CameraServiceDeviceUriTests
{
    private const string OriginalUri = "http://10.0.0.5/onvif/device_service";

    /// <summary>Answers just enough of the probe sequence for ProbeAsync to succeed against whatever
    /// host the client was actually pointed at — the tests care about UpdateAsync's own field
    /// changes, not probing behavior, which CameraServiceChannelTests already covers in depth.</summary>
    private sealed class OnvifStub : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var payload = body.Contains("GetDeviceInformation") ? DeviceInformation()
                : body.Contains("GetCapabilities") ? Capabilities()
                : body.Contains("GetServices") ? "<tds:GetServicesResponse/>"
                : body.Contains("GetProfiles") ? Profiles()
                : body.Contains("GetStreamUri") ? StreamUri()
                : "<Empty/>";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Envelope(payload), System.Text.Encoding.UTF8, "text/xml")
            };
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
              <tds:Manufacturer>Generic</tds:Manufacturer>
              <tds:Model>Cam-1</tds:Model>
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

        private static string Profiles() => """
            <trt:GetProfilesResponse>
              <trt:Profiles token="p1">
                <tt:Name>MainStream</tt:Name>
                <tt:VideoEncoderConfiguration>
                  <tt:Encoding>H264</tt:Encoding>
                  <tt:Resolution><tt:Width>1920</tt:Width><tt:Height>1080</tt:Height></tt:Resolution>
                </tt:VideoEncoderConfiguration>
              </trt:Profiles>
            </trt:GetProfilesResponse>
            """;

        private static string StreamUri() => """
            <trt:GetStreamUriResponse>
              <trt:MediaUri><tt:Uri>rtsp://10.0.0.5/p1</tt:Uri></trt:MediaUri>
            </trt:GetStreamUriResponse>
            """;
    }

    private static (ApplicationDbContext Db, CameraService Service) NewService()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new ApplicationDbContext(options);
        return (db, new CameraService(db, () => new HttpClient(new OnvifStub())));
    }

    private static async Task<Camera> SeedCameraAsync(ApplicationDbContext db)
    {
        var camera = new Camera
        {
            Id = Guid.NewGuid(),
            Name = "Front Door",
            Host = "10.0.0.5",
            OnvifPort = 80,
            DeviceServiceUri = OriginalUri
        };
        db.Cameras.Add(camera);
        await db.SaveChangesAsync();
        return camera;
    }

    [Fact]
    public async Task ChangingTheSchemeUpdatesTheStoredUriAndHost()
    {
        var (db, service) = NewService();
        var camera = await SeedCameraAsync(db);

        await service.UpdateAsync(camera.Id, "Front Door", null, null, null, true, null,
            deviceServiceUri: "https://10.0.0.5/onvif/device_service");

        var updated = await db.Cameras.AsNoTracking().FirstAsync(c => c.Id == camera.Id);
        Assert.Equal("https://10.0.0.5/onvif/device_service", updated.DeviceServiceUri);
        Assert.Equal("10.0.0.5", updated.Host);
        // Uri.Port resolves https's real default (443) rather than carrying forward http's 80 —
        // AddAsync's own doc comment calls out exactly this as a bug it was written to avoid.
        Assert.Equal(443, updated.OnvifPort);
    }

    [Fact]
    public async Task ChangingTheAddressRecomputesHostAndPort()
    {
        var (db, service) = NewService();
        var camera = await SeedCameraAsync(db);

        await service.UpdateAsync(camera.Id, "Front Door", null, null, null, true, null,
            deviceServiceUri: "http://10.0.0.9:8080/onvif/device_service");

        var updated = await db.Cameras.AsNoTracking().FirstAsync(c => c.Id == camera.Id);
        Assert.Equal("10.0.0.9", updated.Host);
        Assert.Equal(8080, updated.OnvifPort);
    }

    [Fact]
    public async Task LeavingTheFieldBlankKeepsTheExistingAddress()
    {
        var (db, service) = NewService();
        var camera = await SeedCameraAsync(db);

        await service.UpdateAsync(camera.Id, "Front Door", null, null, null, true, null,
            deviceServiceUri: null);

        var updated = await db.Cameras.AsNoTracking().FirstAsync(c => c.Id == camera.Id);
        Assert.Equal(OriginalUri, updated.DeviceServiceUri);
        Assert.Equal("10.0.0.5", updated.Host);
    }

    [Fact]
    public async Task SubmittingTheUnchangedValueDoesNotReprobe()
    {
        // Same value round-tripped from the form (not blank) must be a no-op, not a probe on every
        // ordinary save — the equality check exists specifically for this case.
        var (db, service) = NewService();
        var camera = await SeedCameraAsync(db);

        await service.UpdateAsync(camera.Id, "Front Door", null, null, null, true, null,
            deviceServiceUri: OriginalUri);

        var updated = await db.Cameras.AsNoTracking().FirstAsync(c => c.Id == camera.Id);
        Assert.Null(updated.LastProbedAt);
    }

    [Fact]
    public async Task ARealChangeTriggersAReprobe()
    {
        var (db, service) = NewService();
        var camera = await SeedCameraAsync(db);

        await service.UpdateAsync(camera.Id, "Front Door", null, null, null, true, null,
            deviceServiceUri: "https://10.0.0.5/onvif/device_service");

        var updated = await db.Cameras.AsNoTracking().FirstAsync(c => c.Id == camera.Id);
        Assert.NotNull(updated.LastProbedAt);
        Assert.Equal("Generic", updated.Manufacturer);
    }

    [Fact]
    public async Task AnInvalidUriThrowsRatherThanSavingGarbage()
    {
        var (db, service) = NewService();
        var camera = await SeedCameraAsync(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateAsync(camera.Id, "Front Door", null, null, null, true, null,
                deviceServiceUri: "not a uri"));

        var unchanged = await db.Cameras.AsNoTracking().FirstAsync(c => c.Id == camera.Id);
        Assert.Equal(OriginalUri, unchanged.DeviceServiceUri);
    }
}
