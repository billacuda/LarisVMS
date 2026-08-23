using System.Net;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>
/// The built-in "All Cameras" group: seeded once, on every camera unconditionally, exempt from
/// CameraGroupPolicy's single-site rule, and never deletable. See CameraGroup.AllCamerasId's own doc
/// comment for why a fixed sentinel id rather than a new column.
/// </summary>
public class AllCamerasGroupTests
{
    /// <summary>Just enough of the probe sequence for ProbeAsync to succeed instantly — AddAsync
    /// always probes a freshly created camera, and a real HttpClient against an unreachable host would
    /// hang until its ~100s default timeout before ProbeAsync's own catch recovers. Same minimal shape
    /// CameraServiceDeviceUriTests' own OnvifStub uses.</summary>
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

    private static (ApplicationDbContext Db, CameraService CameraService, CameraGroupService GroupService) NewHarness()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new ApplicationDbContext(options);
        return (db, new CameraService(db, () => new HttpClient(new OnvifStub())), new CameraGroupService(db));
    }

    private static async Task SeedAllCamerasGroupAsync(ApplicationDbContext db)
    {
        db.CameraGroups.Add(new CameraGroup
        {
            Id = CameraGroup.AllCamerasId,
            Name = "All Cameras",
            ParentId = null,
            MaterializedPath = $"/{CameraGroup.AllCamerasId}/"
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task DeleteAsyncRefusesToDeleteTheBuiltInGroup()
    {
        var (db, _, groupService) = NewHarness();
        await SeedAllCamerasGroupAsync(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() => groupService.DeleteAsync(CameraGroup.AllCamerasId));
    }

    [Fact]
    public async Task AddAsyncPutsTheNewCameraInTheBuiltInGroup()
    {
        var (db, cameraService, _) = NewHarness();
        await SeedAllCamerasGroupAsync(db);

        var camera = await cameraService.AddAsync(new AddCameraRequest(
            "Front Door", "http://10.0.0.5/onvif/device_service", null, null, GroupId: null));

        var reloaded = await db.Cameras.Include(c => c.Groups).FirstAsync(c => c.Id == camera.Id);
        Assert.Contains(reloaded.Groups, g => g.Id == CameraGroup.AllCamerasId);
    }

    [Fact]
    public async Task SetCameraGroupsAsyncAlwaysKeepsTheBuiltInGroupEvenWhenNotRequested()
    {
        var (db, cameraService, _) = NewHarness();
        await SeedAllCamerasGroupAsync(db);
        var camera = new Camera { Id = Guid.NewGuid(), Name = "Cam", Host = "10.0.0.5", DeviceServiceUri = "http://10.0.0.5/onvif" };
        db.Cameras.Add(camera);
        await db.SaveChangesAsync();

        // Deliberately an empty list — nothing requests the built-in group, and it must still end up
        // in the final membership because it's not actually removable.
        await cameraService.SetCameraGroupsAsync(camera.Id, []);

        var reloaded = await db.Cameras.Include(c => c.Groups).FirstAsync(c => c.Id == camera.Id);
        Assert.Contains(reloaded.Groups, g => g.Id == CameraGroup.AllCamerasId);
    }

    [Fact]
    public async Task SetCameraGroupsAsyncDoesNotCountTheBuiltInGroupTowardTheSingleSiteRule()
    {
        var (db, cameraService, groupService) = NewHarness();
        await SeedAllCamerasGroupAsync(db);
        var site = await groupService.CreateAsync("Site A", parentId: null);
        var camera = new Camera { Id = Guid.NewGuid(), Name = "Cam", Host = "10.0.0.5", DeviceServiceUri = "http://10.0.0.5/onvif" };
        db.Cameras.Add(camera);
        await db.SaveChangesAsync();

        // A real site plus the built-in group — naively checking "do all these ids share one site"
        // would see two different top-level ids (the built-in group and Site A) and reject this.
        await cameraService.SetCameraGroupsAsync(camera.Id, [site.Id, CameraGroup.AllCamerasId]);

        var reloaded = await db.Cameras.Include(c => c.Groups).FirstAsync(c => c.Id == camera.Id);
        Assert.Contains(reloaded.Groups, g => g.Id == site.Id);
        Assert.Contains(reloaded.Groups, g => g.Id == CameraGroup.AllCamerasId);
    }

    [Fact]
    public async Task SeedServiceCreatesTheGroupOnceAndBackfillsExistingCamerasIdempotently()
    {
        var (db, _, _) = NewHarness();
        db.Cameras.Add(new Camera { Id = Guid.NewGuid(), Name = "Cam 1", Host = "10.0.0.5", DeviceServiceUri = "http://10.0.0.5/onvif" });
        db.Cameras.Add(new Camera { Id = Guid.NewGuid(), Name = "Cam 2", Host = "10.0.0.6", DeviceServiceUri = "http://10.0.0.6/onvif" });
        await db.SaveChangesAsync();

        var seeder = new CameraGroupSeedService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<CameraGroupSeedService>.Instance);
        await seeder.SeedAsync();

        var group = await db.CameraGroups.Include(g => g.Cameras).SingleAsync(g => g.Id == CameraGroup.AllCamerasId);
        Assert.Equal("All Cameras", group.Name);
        Assert.Equal(2, group.Cameras.Count);

        // A camera added between seeder runs, plus running the seeder again — the group isn't
        // recreated (still exactly one row) and the new camera is picked up without disturbing the
        // existing links.
        db.Cameras.Add(new Camera { Id = Guid.NewGuid(), Name = "Cam 3", Host = "10.0.0.7", DeviceServiceUri = "http://10.0.0.7/onvif" });
        await db.SaveChangesAsync();
        await seeder.SeedAsync();

        Assert.Equal(1, await db.CameraGroups.CountAsync(g => g.Id == CameraGroup.AllCamerasId));
        var reloaded = await db.CameraGroups.Include(g => g.Cameras).SingleAsync(g => g.Id == CameraGroup.AllCamerasId);
        Assert.Equal(3, reloaded.Cameras.Count);
    }
}
