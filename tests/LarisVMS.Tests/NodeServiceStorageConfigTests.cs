using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>
/// Storage config is per-node with no global default (0.188.0). GetConfigAsync must send the node's
/// own StorageRootPath / ArchiveRootPath, and must serve NO camera config to a node that has no
/// storage path set — it records nothing until an admin configures one.
/// </summary>
public class NodeServiceStorageConfigTests
{
    private static async Task<(NodeService Service, Guid NodeId)> SeedAsync(string? storageRoot, string? archiveRoot = null)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new ApplicationDbContext(options);

        var node = new LarisVMS.Core.Entities.Node
        {
            Id = Guid.NewGuid(), Name = "node-1", ApiKeyHash = "hash", MediaSigningKey = "key",
            StorageRootPath = storageRoot, ArchiveRootPath = archiveRoot,
        };
        var camera = new Camera
        {
            Id = Guid.NewGuid(), Name = "front-door", Host = "10.0.0.1",
            DeviceServiceUri = "http://10.0.0.1/onvif/device_service", NodeId = node.Id, IsEnabled = true,
        };
        db.AddRange(node, camera);
        await db.SaveChangesAsync();

        return (new NodeService(db, new SettingsResolver(db)), node.Id);
    }

    [Fact]
    public async Task SendsTheNodesOwnStorageAndArchivePaths()
    {
        var (service, nodeId) = await SeedAsync(@"E:\LarisVMS\recordings", @"\\nas1\archive$\LarisVMS");

        var config = await service.GetConfigAsync(nodeId);

        Assert.Equal(@"E:\LarisVMS\recordings", config.StorageRootPath);
        Assert.Equal(@"\\nas1\archive$\LarisVMS", config.ArchiveRootPath);
        Assert.Single(config.Cameras);
    }

    [Fact]
    public async Task NullArchivePathIsSentAsNull()
    {
        var (service, nodeId) = await SeedAsync(@"E:\LarisVMS\recordings");

        var config = await service.GetConfigAsync(nodeId);

        Assert.Null(config.ArchiveRootPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ServesNoCameraConfigWhenTheNodeHasNoStoragePath(string? storageRoot)
    {
        var (service, nodeId) = await SeedAsync(storageRoot);

        var config = await service.GetConfigAsync(nodeId);

        Assert.Empty(config.Cameras);
        Assert.Empty(config.OrphanedCameras);
        Assert.True(string.IsNullOrWhiteSpace(config.StorageRootPath));
    }
}
