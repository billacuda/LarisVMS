using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

public sealed class NodeBuildSupersedeTests : IDisposable
{
    private readonly ApplicationDbContext _db = new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public void Dispose() => _db.Dispose();

    private NodeBuildVersion Add(string version, string platform, NodeBuildStatus status, int minutesAgo = 0)
    {
        var build = new NodeBuildVersion
        {
            Version = version, Platform = platform, Status = status,
            UploadedAt = DateTime.UtcNow.AddMinutes(-minutesAgo),
            FilePath = "x", Sha256 = new string('0', 64),
        };
        _db.NodeBuildVersions.Add(build);
        return build;
    }

    private async Task<NodeBuildStatus> StatusOf(NodeBuildVersion b) =>
        (await _db.NodeBuildVersions.AsNoTracking().SingleAsync(x => x.Id == b.Id)).Status;

    [Fact]
    public async Task KeepsOnlyTheNewestPendingPerPlatform()
    {
        // Registered out of order on purpose: the version decides, not the registration time.
        var nodeNew = Add("0.213.0.10", "win-x64", NodeBuildStatus.Pending, minutesAgo: 30);
        var nodeOld = Add("0.212.0.9", "win-x64", NodeBuildStatus.Pending, minutesAgo: 5);
        var nodeOlder = Add("0.211.0", "win-x64", NodeBuildStatus.Pending, minutesAgo: 60);
        var proxyNew = Add("0.213.0.11", "proxy-win-x64", NodeBuildStatus.Pending);
        var proxyOld = Add("0.212.0.8", "proxy-win-x64", NodeBuildStatus.Pending);
        await _db.SaveChangesAsync();

        var superseded = await new NodeBuildService(_db).SupersedeOutdatedPendingAsync();

        Assert.Equal(3, superseded);
        Assert.Equal(NodeBuildStatus.Pending, await StatusOf(nodeNew));
        Assert.Equal(NodeBuildStatus.Superseded, await StatusOf(nodeOld));
        Assert.Equal(NodeBuildStatus.Superseded, await StatusOf(nodeOlder));
        Assert.Equal(NodeBuildStatus.Pending, await StatusOf(proxyNew));
        Assert.Equal(NodeBuildStatus.Superseded, await StatusOf(proxyOld));
    }

    [Fact]
    public async Task SupersedesPendingThatIsNotNewerThanApproved()
    {
        Add("0.213.0", "win-x64", NodeBuildStatus.Approved);
        var older = Add("0.212.0", "win-x64", NodeBuildStatus.Pending);
        var same = Add("0.213.0", "win-x64", NodeBuildStatus.Pending);
        await _db.SaveChangesAsync();

        await new NodeBuildService(_db).SupersedeOutdatedPendingAsync();

        Assert.Equal(NodeBuildStatus.Superseded, await StatusOf(older));
        Assert.Equal(NodeBuildStatus.Superseded, await StatusOf(same));
    }

    [Fact]
    public async Task ApprovingSupersedesOlderPending_AndASupersededBuildCantBeApproved()
    {
        var older = Add("0.212.0", "win-x64", NodeBuildStatus.Pending);
        var newer = Add("0.213.0", "win-x64", NodeBuildStatus.Pending);
        await _db.SaveChangesAsync();
        var service = new NodeBuildService(_db);

        // Approve the newer one directly, while the older is still pending.
        await service.ApproveAsync(newer.Id, "admin");

        Assert.Equal(NodeBuildStatus.Approved, await StatusOf(newer));
        Assert.Equal(NodeBuildStatus.Superseded, await StatusOf(older));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApproveAsync(older.Id, "admin"));
        Assert.Equal("0.213.0", (await service.GetLatestForPlatformAsync("win-x64"))!.Version);
    }
}
