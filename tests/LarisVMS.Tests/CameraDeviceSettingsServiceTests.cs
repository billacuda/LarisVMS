using System.Net;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;
using LarisVMS.Onvif.Clients;

namespace LarisVMS.Tests;

/// <summary>CameraDeviceSettingsService: validation against what the camera reports, and bulk apply's
/// same-model rule, mapping by stream role, and per-camera results (one camera failing must not stop
/// the rest). Camera traffic is stubbed per host.</summary>
public class CameraDeviceSettingsServiceTests
{
    // ── Validation ──────────────────────────────────────────────────────────

    private static readonly OnvifVideoEncoderConfig H264Config = new(
        "VE_1", "Main", 1, "H264", new OnvifResolution(1920, 1080), 4, 25, 1, 4096, null, 50, "Main");

    private static readonly OnvifVideoEncodingOptions H264Options = new(
        "H264", [new(1920, 1080), new(1280, 720)], new OnvifRange(1, 30), [], new OnvifRange(64, 8192),
        new OnvifRange(1, 150), new OnvifRange(1, 6), ["Main", "High"], ConstantBitRateSupported: true);

    private static readonly OnvifVideoEncodingOptions H265Options = H264Options with
    {
        Encoding = "H265", Resolutions = [new(1280, 720)], FrameRateRange = null, FrameRatesSupported = [25, 15], Profiles = ["Main"],
    };

    [Fact]
    public void ValidVideoChangeMapsToTheOnvifChange()
    {
        var change = CameraDeviceSettingsService.ValidateVideo(
            new VideoEncoderValues(Width: 1280, Height: 720, FrameRate: 15, BitrateKbps: 2048, RateControl: "cbr", Profile: "high"),
            H264Config, [H264Options], viaMedia2: true);

        Assert.Equal(new OnvifResolution(1280, 720), change.Resolution);
        Assert.Equal(15, change.FrameRateLimit);
        Assert.Equal(2048, change.BitrateLimitKbps);
        Assert.True(change.ConstantBitRate);
        Assert.Equal("High", change.Profile); // the camera's own spelling
        Assert.Null(change.Encoding);
    }

    [Theory]
    [InlineData(100000, null, null, null)]   // bitrate above range
    [InlineData(null, 800, 600, null)]       // resolution not offered
    [InlineData(null, null, null, 60.0)]     // frame rate above range
    public void OutOfRangeVideoValuesAreRejected(int? kbps, int? width, int? height, double? fps)
    {
        Assert.Throws<DeviceSettingsValidationException>(() => CameraDeviceSettingsService.ValidateVideo(
            new VideoEncoderValues(BitrateKbps: kbps, Width: width, Height: height, FrameRate: fps), H264Config, [H264Options], viaMedia2: true));
    }

    [Fact]
    public void CbrIsRejectedOverMedia1()
    {
        var ex = Assert.Throws<DeviceSettingsValidationException>(() => CameraDeviceSettingsService.ValidateVideo(
            new VideoEncoderValues(RateControl: "CBR"), H264Config, [H264Options], viaMedia2: false));
        Assert.Contains("CBR", ex.Message);
    }

    [Fact]
    public void SwitchingCodecChecksTheNewCodecsOptions()
    {
        // 1920×1080 isn't offered for H.265 here, so the switch needs a resolution too.
        Assert.Throws<DeviceSettingsValidationException>(() => CameraDeviceSettingsService.ValidateVideo(
            new VideoEncoderValues(Codec: "H265"), H264Config, [H264Options, H265Options], viaMedia2: true));

        var ok = CameraDeviceSettingsService.ValidateVideo(
            new VideoEncoderValues(Codec: "h265", Width: 1280, Height: 720, FrameRate: 15), H264Config, [H264Options, H265Options], viaMedia2: true);
        Assert.Equal("H265", ok.Encoding);

        // A frame rate the list doesn't contain.
        Assert.Throws<DeviceSettingsValidationException>(() => CameraDeviceSettingsService.ValidateVideo(
            new VideoEncoderValues(Codec: "H265", Width: 1280, Height: 720, FrameRate: 20), H264Config, [H264Options, H265Options], viaMedia2: true));
    }

    [Fact]
    public void ImageSettingsTheCameraDoesNotReportAreRejected()
    {
        var options = new OnvifImagingOptions(new OnvifRange(0, 100), null, null, null, ["AUTO", "ON", "OFF"],
            [], null, [], null, [], [], null, null);

        var change = CameraDeviceSettingsService.ValidateImage(new ImageSettingsValues(Brightness: 70, IrCutFilter: "auto"), options);
        Assert.Equal(70, change.Brightness);
        Assert.Equal("AUTO", change.IrCutFilter);

        Assert.Throws<DeviceSettingsValidationException>(() => CameraDeviceSettingsService.ValidateImage(new ImageSettingsValues(Contrast: 10), options));
        Assert.Throws<DeviceSettingsValidationException>(() => CameraDeviceSettingsService.ValidateImage(new ImageSettingsValues(Brightness: 150), options));
        Assert.Throws<DeviceSettingsValidationException>(() => CameraDeviceSettingsService.ValidateImage(new ImageSettingsValues(WdrMode: "ON"), options));
    }

    [Fact]
    public void AudioValuesMustBeInTheCodecsLists()
    {
        var current = new OnvifAudioEncoderConfig("AE_1", "Audio", "AAC", 48, 16);
        IReadOnlyList<OnvifAudioEncodingOptions> options = [new("AAC", [32, 48, 64], [16, 48]), new("G711", [64], [8])];

        Assert.Equal(64, CameraDeviceSettingsService.ValidateAudio(new AudioEncoderValues(BitrateKbps: 64), current, options).BitrateKbps);
        Assert.Throws<DeviceSettingsValidationException>(() => CameraDeviceSettingsService.ValidateAudio(new AudioEncoderValues(BitrateKbps: 96), current, options));
        Assert.Throws<DeviceSettingsValidationException>(() => CameraDeviceSettingsService.ValidateAudio(new AudioEncoderValues(Codec: "G726"), current, options));
    }

    // ── Bulk ────────────────────────────────────────────────────────────────

    /// <summary>Answers every request to a host with GetProfiles (Main and Sub only), or throws for
    /// hosts listed as unreachable.</summary>
    private sealed class PerHostHandler(params string[] unreachableHosts) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (unreachableHosts.Contains(request.RequestUri!.Host)) throw new HttpRequestException("No route to host.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    <env:Envelope xmlns:env="http://www.w3.org/2003/05/soap-envelope" xmlns:trt="http://www.onvif.org/ver10/media/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema">
                      <env:Body><trt:GetProfilesResponse>
                        <trt:Profiles token="P1"><tt:Name>Main</tt:Name><tt:VideoEncoderConfiguration token="VE_1"><tt:Encoding>H264</tt:Encoding></tt:VideoEncoderConfiguration></trt:Profiles>
                        <trt:Profiles token="P2"><tt:Name>Sub</tt:Name><tt:VideoEncoderConfiguration token="VE_2"><tt:Encoding>H264</tt:Encoding></tt:VideoEncoderConfiguration></trt:Profiles>
                      </trt:GetProfilesResponse></env:Body>
                    </env:Envelope>
                    """, System.Text.Encoding.UTF8, "text/xml")
            });
        }
    }

    private static (ApplicationDbContext Db, CameraDeviceSettingsService Service) NewService(HttpMessageHandler handler)
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Func<HttpClient> http = () => new HttpClient(handler);
        return (db, new CameraDeviceSettingsService(db, http, new CameraService(db, http)));
    }

    private static Camera AddCamera(ApplicationDbContext db, string name, string host, string? make, string? model, bool probed = true)
    {
        var camera = new Camera
        {
            Id = Guid.NewGuid(), Name = name, Host = host, DeviceServiceUri = $"http://{host}/onvif/device_service",
            Manufacturer = make, Model = model, IsEnabled = true, CreatedAt = DateTime.UtcNow
        };
        db.Cameras.Add(camera);
        if (probed)
        {
            db.CameraCapabilities.Add(new CameraCapabilities { CameraId = camera.Id, RawProbeJson = $$"""{"Media":"http://{{host}}/onvif/media_service"}""" });
            db.CameraStreams.Add(new CameraStream { Id = Guid.NewGuid(), CameraId = camera.Id, Role = CameraStreamRole.Main, ProfileToken = "P1", RtspUri = $"rtsp://{host}/main" });
            db.CameraStreams.Add(new CameraStream { Id = Guid.NewGuid(), CameraId = camera.Id, Role = CameraStreamRole.Sub, ProfileToken = "P2", RtspUri = $"rtsp://{host}/sub" });
        }
        db.SaveChanges();
        return camera;
    }

    [Fact]
    public async Task BulkRejectsCamerasOfDifferentModels()
    {
        var (db, service) = NewService(new PerHostHandler());
        var a = AddCamera(db, "A", "10.0.0.1", "Acme", "B4K");
        var b = AddCamera(db, "B", "10.0.0.2", "Acme", "D2M");

        await Assert.ThrowsAsync<DeviceSettingsValidationException>(() => service.ApplyBulkAsync(
            new BulkDeviceSettingsRequest([a.Id, b.Id], Video: new() { ["Main"] = new VideoEncoderValues(FrameRate: 15) })));
    }

    [Fact]
    public async Task BulkRejectsCamerasWithNoReportedModel()
    {
        var (db, service) = NewService(new PerHostHandler());
        var a = AddCamera(db, "A", "10.0.0.1", null, null);

        await Assert.ThrowsAsync<DeviceSettingsValidationException>(() => service.ApplyBulkAsync(
            new BulkDeviceSettingsRequest([a.Id], Video: new() { ["Main"] = new VideoEncoderValues(FrameRate: 15) })));
    }

    [Fact]
    public async Task BulkReportsEachCameraAndKeepsGoingAfterAFailure()
    {
        var (db, service) = NewService(new PerHostHandler("10.0.0.2"));
        var reachable = AddCamera(db, "Reachable", "10.0.0.1", "Acme", "B4K");
        var offline = AddCamera(db, "Offline", "10.0.0.2", "ACME ", "b4k"); // make/model match ignores case and spacing
        var unprobed = AddCamera(db, "Unprobed", "10.0.0.3", "Acme", "B4K", probed: false);

        // No camera has a Third stream, so the reachable one skips it rather than failing.
        var results = await service.ApplyBulkAsync(new BulkDeviceSettingsRequest(
            [reachable.Id, offline.Id, unprobed.Id], Video: new() { ["Third"] = new VideoEncoderValues(FrameRate: 15) }));

        Assert.Equal(3, results.Count);
        var r = results.Single(x => x.CameraId == reachable.Id);
        Assert.Equal(BulkDeviceSettingsStatus.Skipped, r.Status);
        Assert.Contains(r.Messages, m => m.Contains("no Third stream"));
        Assert.Equal(BulkDeviceSettingsStatus.Failed, results.Single(x => x.CameraId == offline.Id).Status);
        Assert.Equal(BulkDeviceSettingsStatus.Skipped, results.Single(x => x.CameraId == unprobed.Id).Status);
    }

    [Fact]
    public async Task PeersAreOtherCamerasOfTheSameModel()
    {
        var (db, service) = NewService(new PerHostHandler());
        var a = AddCamera(db, "A", "10.0.0.1", "Acme", "B4K");
        var b = AddCamera(db, "B", "10.0.0.2", "acme", "B4K");
        AddCamera(db, "C", "10.0.0.3", "Acme", "D2M");
        AddCamera(db, "D", "10.0.0.4", null, null);

        var peers = await service.GetSameModelPeersAsync(a.Id);

        Assert.Equal(b.Id, Assert.Single(peers).CameraId);
    }
}
