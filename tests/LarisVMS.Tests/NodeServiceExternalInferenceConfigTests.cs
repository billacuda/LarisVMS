using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>
/// The external HTTP inference backend's node-scoped config (Detection.Backend + the service
/// address/model/input-size) must flow through GetConfigAsync onto NodeConfigResponse, resolved
/// Node &rarr; Global like Detection.ModelFamily, with the input size re-clamped to a positive
/// multiple of 32.
/// </summary>
public class NodeServiceExternalInferenceConfigTests
{
    private static async Task<(ApplicationDbContext Db, NodeService Service, SettingsResolver Settings, Guid NodeId)> SeedAsync()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new ApplicationDbContext(options);

        var node = new LarisVMS.Core.Entities.Node
        {
            Id = Guid.NewGuid(), Name = "node-1", ApiKeyHash = "hash", MediaSigningKey = "key",
            StorageRootPath = @"E:\LarisVMS\recordings",
        };
        db.Add(node);
        await db.SaveChangesAsync();

        var settings = new SettingsResolver(db);
        return (db, new NodeService(db, settings), settings, node.Id);
    }

    [Fact]
    public async Task DefaultsToTheBuiltInBackend()
    {
        var (_, service, _, nodeId) = await SeedAsync();

        var config = await service.GetConfigAsync(nodeId);

        Assert.Equal("BuiltIn", config.DetectionBackend);
        Assert.Equal("", config.ExternalInferenceUrl);
        Assert.Equal("", config.ExternalInferenceModel);
        Assert.Equal(640, config.ExternalInferenceInputSize);
    }

    [Fact]
    public async Task SendsTheGloballyConfiguredExternalBackend()
    {
        var (_, service, settings, nodeId) = await SeedAsync();
        await settings.SetGlobalAsync("Detection.Backend", "ExternalHttp", "test");
        await settings.SetGlobalAsync("Detection.ExternalInferenceUrl", "http://192.168.1.50:8080", "test");
        await settings.SetGlobalAsync("Detection.ExternalInferenceModel", "yolov8n", "test");
        await settings.SetGlobalAsync("Detection.ExternalInferenceInputSize", "512", "test");

        var config = await service.GetConfigAsync(nodeId);

        Assert.Equal("ExternalHttp", config.DetectionBackend);
        Assert.Equal("http://192.168.1.50:8080", config.ExternalInferenceUrl);
        Assert.Equal("yolov8n", config.ExternalInferenceModel);
        Assert.Equal(512, config.ExternalInferenceInputSize);
    }

    [Fact]
    public async Task ANodeOverrideBeatsTheGlobalValue()
    {
        var (_, service, settings, nodeId) = await SeedAsync();
        await settings.SetGlobalAsync("Detection.Backend", "BuiltIn", "test");
        await settings.SetOverrideAsync(SettingScope.Node, nodeId, "Detection.Backend", "ExternalHttp", "test");
        await settings.SetOverrideAsync(SettingScope.Node, nodeId, "Detection.ExternalInferenceUrl", "http://10.0.0.9:9000", "test");

        var config = await service.GetConfigAsync(nodeId);

        Assert.Equal("ExternalHttp", config.DetectionBackend);
        Assert.Equal("http://10.0.0.9:9000", config.ExternalInferenceUrl);
    }

    [Theory]
    [InlineData("500")]   // not a multiple of 32
    [InlineData("0")]
    [InlineData("-64")]
    [InlineData("garbage")]
    public async Task AnInvalidInputSizeFallsBackTo640(string stored)
    {
        var (_, service, settings, nodeId) = await SeedAsync();
        await settings.SetGlobalAsync("Detection.ExternalInferenceInputSize", stored, "test");

        var config = await service.GetConfigAsync(nodeId);

        Assert.Equal(640, config.ExternalInferenceInputSize);
    }

    [Fact]
    public async Task AValidNonDefaultInputSizeIsKept()
    {
        var (_, service, settings, nodeId) = await SeedAsync();
        await settings.SetGlobalAsync("Detection.ExternalInferenceInputSize", "1280", "test");

        var config = await service.GetConfigAsync(nodeId);

        Assert.Equal(1280, config.ExternalInferenceInputSize);
    }

    [Fact]
    public async Task DefaultsToNoApiKey()
    {
        var (_, service, _, nodeId) = await SeedAsync();

        var config = await service.GetConfigAsync(nodeId);

        Assert.Equal("", config.ExternalInferenceApiKey);
    }

    [Fact]
    public async Task ANodeApiKeyOverrideBeatsTheGlobalKey()
    {
        var (_, service, settings, nodeId) = await SeedAsync();
        await settings.SetGlobalAsync("Detection.ExternalInferenceApiKey", "global-key", "test");
        await settings.SetOverrideAsync(SettingScope.Node, nodeId, "Detection.ExternalInferenceApiKey", "node-key", "test");

        var config = await service.GetConfigAsync(nodeId);

        Assert.Equal("node-key", config.ExternalInferenceApiKey);
    }
}
