using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>The Nodes page's "Remove old builds": everything older than each platform's approved
/// build goes, with its files; the approved build, anything newer, and other folders' files stay.</summary>
public sealed class NodeBuildPurgeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "larisvms-purge-" + Guid.NewGuid().ToString("N"));
    private readonly ApplicationDbContext _db = new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private string Builds => Path.Combine(_root, "node-builds");

    public NodeBuildPurgeTests() => Directory.CreateDirectory(Builds);

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private NodeBuildService NewService() => new(_db) { BuildsRoot = Builds };

    private NodeBuildVersion Add(string platform, string version, NodeBuildStatus status, bool vision = false)
    {
        var build = new NodeBuildVersion { Platform = platform, Version = version, Status = status, Sha256 = new string('a', 64) };
        build.FilePath = Path.Combine(Builds, $"{build.Id}.exe");
        File.WriteAllText(build.FilePath, "12345");
        if (vision)
        {
            build.VisionFilePath = Path.Combine(Builds, $"{build.Id}.vision.exe");
            File.WriteAllText(build.VisionFilePath, "123");
            build.BackendsFilePath = Path.Combine(Builds, $"{build.Id}.onnxruntime-backends.zip");
            File.WriteAllText(build.BackendsFilePath, "12");
        }
        _db.NodeBuildVersions.Add(build);
        _db.SaveChanges();
        return build;
    }

    [Fact]
    public async Task RemovesOlderBuildsAndTheirFiles_KeepsApprovedAndNewer()
    {
        var old1 = Add("win-x64", "0.220.0.600", NodeBuildStatus.Approved, vision: true);
        var old2 = Add("win-x64", "0.221.0.610", NodeBuildStatus.Superseded);
        var current = Add("win-x64", "0.222.0.620", NodeBuildStatus.Approved, vision: true);
        var waiting = Add("win-x64", "0.223.0.630", NodeBuildStatus.Pending);
        var oldProxy = Add("proxy-win-x64", "0.220.0.600", NodeBuildStatus.Rejected);
        var proxy = Add("proxy-win-x64", "0.222.0.620", NodeBuildStatus.Approved);

        Assert.Equal(3, NodeBuildService.Purgeable(await _db.NodeBuildVersions.ToListAsync()).Count);
        var result = await NewService().PurgeOldBuildsAsync();

        Assert.Equal(3, result.Builds);
        Assert.Equal(5, result.Files);
        Assert.Equal(5 + 3 + 2 + 5 + 5, result.Bytes);
        var left = await _db.NodeBuildVersions.Select(b => b.Id).ToListAsync();
        Assert.Equal(new[] { current.Id, waiting.Id, proxy.Id }.Order(), left.Order());
        Assert.False(File.Exists(old1.FilePath));
        Assert.False(File.Exists(old1.BackendsFilePath));
        Assert.False(File.Exists(old2.FilePath));
        Assert.False(File.Exists(oldProxy.FilePath));
        Assert.True(File.Exists(current.FilePath));
        Assert.True(File.Exists(current.VisionFilePath));
        Assert.True(File.Exists(waiting.FilePath));
    }

    [Fact]
    public async Task APlatformWithNoApprovedBuild_IsLeftAlone()
    {
        Add("linux-x64", "0.220.0.600", NodeBuildStatus.Superseded);
        Add("linux-x64", "0.222.0.620", NodeBuildStatus.Pending);

        var result = await NewService().PurgeOldBuildsAsync();

        Assert.Equal(0, result.Builds);
        Assert.Equal(2, await _db.NodeBuildVersions.CountAsync());
    }

    [Fact]
    public async Task StrayFilesInTheBuildsFolder_AreRemoved_OthersAreNot()
    {
        var current = Add("win-x64", "0.222.0.620", NodeBuildStatus.Approved);
        var stray = Path.Combine(Builds, Guid.NewGuid() + ".exe");
        File.WriteAllText(stray, "1234");
        var outside = Path.Combine(_root, "elsewhere.exe");
        File.WriteAllText(outside, "x");
        var elsewhere = Add("win-x64", "0.100.0.1", NodeBuildStatus.Approved);
        File.Delete(elsewhere.FilePath);
        elsewhere.FilePath = outside;
        await _db.SaveChangesAsync();

        var result = await NewService().PurgeOldBuildsAsync();

        Assert.False(File.Exists(stray));
        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(current.FilePath));
        Assert.Equal(1, result.Builds);
        Assert.Equal(1, result.Files);
        Assert.Equal(4, result.Bytes);
    }
}
