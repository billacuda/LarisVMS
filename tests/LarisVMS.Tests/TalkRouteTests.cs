using Microsoft.Extensions.Logging.Abstractions;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;
using LarisVMS.Media.Talk;
using LarisVMS.Node;

namespace LarisVMS.Tests;

/// <summary>Which way two-way audio reaches a camera's speaker, by the camera's Talk.Method.</summary>
public class TalkRouteTests
{
    private static readonly Uri Rtsp = new("rtsp://10.0.0.5:554/cam/realmonitor?channel=1&subtype=0");

    private static NodeConfigCameraDto Camera(string talkMethod, string? integrationKey = null, string? integrationBase = null,
        string? deviceBase = "http://10.0.0.5:80") =>
        new(Guid.NewGuid(), "Doorbell", "admin", "pw", [], null, null, [], "Continuous", 0, 0, null, [], [],
            IntegrationKey: integrationKey, IntegrationBaseUri: integrationBase, TalkMethod: talkMethod, DeviceBaseUri: deviceBase);

    private sealed class FakeSink(string name) : ITalkSink
    {
        public string Name { get; } = name;
        public string Codec => "PCMA";
        public Task SendPcmAsync(ReadOnlyMemory<byte> pcm16le, CancellationToken ct) => Task.CompletedTask;
        public Task EndTalkspurtAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Route
    {
        public List<string> Tried { get; } = [];
        public Uri? ApiBase { get; private set; }
        public bool OnvifFails { get; init; }

        public Task<ITalkSink> Onvif(Uri uri, CancellationToken ct)
        {
            Tried.Add("onvif");
            return OnvifFails ? throw new BackchannelException("refused") : Task.FromResult<ITalkSink>(new FakeSink("onvif"));
        }

        public Task<ITalkSink> Api(Uri uri, CancellationToken ct)
        {
            Tried.Add("api");
            ApiBase = uri;
            return Task.FromResult<ITalkSink>(new FakeSink("api"));
        }

        public Task<ITalkSink> Open(NodeConfigCameraDto camera) =>
            TalkSessionHandler.OpenSinkAsync(camera, Rtsp, NullLogger.Instance, CancellationToken.None, Onvif, Api);
    }

    [Fact]
    public async Task AutoUsesOnvifWhenItOpens()
    {
        var route = new Route();
        var sink = (FakeSink)await route.Open(Camera("Auto", DahuaCgiIntegrationProvider.ProviderKey, "http://10.0.0.5:80"));
        Assert.Equal("onvif", sink.Name);
        Assert.Equal(["onvif"], route.Tried);
    }

    [Fact]
    public async Task AutoFallsBackToTheDahuaApiWhenOnvifIsRefused()
    {
        var route = new Route { OnvifFails = true };
        var sink = (FakeSink)await route.Open(Camera("Auto", DahuaCgiIntegrationProvider.ProviderKey, "http://10.0.0.5:80"));
        Assert.Equal("api", sink.Name);
        Assert.Equal(["onvif", "api"], route.Tried);
    }

    [Fact]
    public async Task AutoWithoutADahuaIntegrationReportsTheOnvifError()
    {
        var route = new Route { OnvifFails = true };
        await Assert.ThrowsAsync<BackchannelException>(() => route.Open(Camera("Auto")));
        Assert.Equal(["onvif"], route.Tried);
    }

    [Fact]
    public async Task OnvifOnlyNeverTriesTheCameraApi()
    {
        var route = new Route { OnvifFails = true };
        await Assert.ThrowsAsync<BackchannelException>(() => route.Open(Camera("Onvif", DahuaCgiIntegrationProvider.ProviderKey, "http://10.0.0.5:80")));
        Assert.Equal(["onvif"], route.Tried);
    }

    [Fact]
    public async Task CameraApiGoesStraightToItEvenWithoutAnIntegration()
    {
        // The Amcrest doorbell case: ONVIF opens but stays silent, and its manufacturer string may
        // not be recognized, so there's no integration — the device address still serves.
        var route = new Route();
        var sink = (FakeSink)await route.Open(Camera("CameraApi"));
        Assert.Equal("api", sink.Name);
        Assert.Equal(["api"], route.Tried);
        Assert.Equal(new Uri("http://10.0.0.5:80"), route.ApiBase);
    }

    [Fact]
    public async Task CameraApiWithNoAddressSaysSo()
    {
        var route = new Route();
        await Assert.ThrowsAsync<BackchannelException>(() => route.Open(Camera("CameraApi", deviceBase: null)));
        Assert.Empty(route.Tried);
    }
}
