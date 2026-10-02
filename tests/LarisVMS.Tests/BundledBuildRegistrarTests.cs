using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using LarisVMS.Core.Enums;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

public sealed class BundledBuildRegistrarTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "laris-bundled-" + Guid.NewGuid().ToString("N"));
    private readonly ApplicationDbContext _db = new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private string Packages => Path.Combine(_root, "packages");

    private BundledBuildRegistrar NewRegistrar() => new(_db, NullLogger<BundledBuildRegistrar>.Instance)
    {
        NodeBuildsRoot = Path.Combine(_root, "node-builds"),
        VisionNativeRoot = Path.Combine(_root, "vision-native"),
    };

    private void WritePackage(string sub, string exe, string versionFile, string version, string? extra = null)
    {
        var dir = Path.Combine(Packages, sub);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, exe), "binary-" + version);
        File.WriteAllText(Path.Combine(dir, versionFile), version + "\r\n");
        if (extra is not null) File.WriteAllText(Path.Combine(dir, extra), "vision-" + version);
    }

    [Fact]
    public async Task RegistersNodeAndProxyAsPending_WithHashesAndVision()
    {
        WritePackage("node", "LarisVMS.Node.exe", "node-build-version.txt", "0.209.0.400", "LarisVMS.Vision.Service.exe");
        WritePackage("proxy", "LarisVMS.Proxy.exe", "proxy-build-version.txt", "0.209.0.401");

        await NewRegistrar().RegisterAsync(Packages);

        var builds = await _db.NodeBuildVersions.ToListAsync();
        Assert.Equal(2, builds.Count);
        var node = builds.Single(b => b.Platform == BundledBuildRegistrar.NodePlatform);
        Assert.Equal("0.209.0.400", node.Version);
        Assert.Equal(NodeBuildStatus.Pending, node.Status);
        Assert.True(File.Exists(node.FilePath));
        Assert.Equal(64, node.Sha256.Length);
        Assert.NotNull(node.VisionFilePath);
        Assert.True(File.Exists(node.VisionFilePath));

        var proxy = builds.Single(b => b.Platform == BundledBuildRegistrar.ProxyPlatform);
        Assert.Null(proxy.VisionFilePath);
    }

    [Fact]
    public async Task SecondRun_IsIdempotent()
    {
        WritePackage("node", "LarisVMS.Node.exe", "node-build-version.txt", "0.209.0.400");

        await NewRegistrar().RegisterAsync(Packages);
        await NewRegistrar().RegisterAsync(Packages);

        Assert.Equal(1, await _db.NodeBuildVersions.CountAsync());
    }

    [Fact]
    public async Task MissingPackages_RegisterNothing()
    {
        await NewRegistrar().RegisterAsync(Packages);
        Assert.Equal(0, await _db.NodeBuildVersions.CountAsync());
    }

    [Fact]
    public async Task SeedsCudaProvider_OnlyWhenChanged()
    {
        var src = Path.Combine(Packages, "cuda-provider", "onnxruntime_providers_cuda.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        File.WriteAllText(src, "cuda-v1");

        var registrar = NewRegistrar();
        await registrar.RegisterAsync(Packages);
        var dest = Path.Combine(registrar.VisionNativeRoot, "onnxruntime_providers_cuda.dll");
        Assert.Equal("cuda-v1", File.ReadAllText(dest));

        File.WriteAllText(src, "cuda-v2");
        await registrar.RegisterAsync(Packages);
        Assert.Equal("cuda-v2", File.ReadAllText(dest));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
