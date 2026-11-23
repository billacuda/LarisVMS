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

    [Fact]
    public async Task RegistersEveryPlatformsBuilds_ForAMixedFleet()
    {
        WritePackage(Path.Combine("node", "win-x64"), "LarisVMS.Node.exe", "node-build-version.txt", "0.223.0.500", "LarisVMS.Vision.Service.exe");
        WritePackage(Path.Combine("node", "linux-x64"), "LarisVMS.Node", "node-build-version.txt", "0.223.0.500", "LarisVMS.Vision.Service");
        WritePackage(Path.Combine("proxy", "win-x64"), "LarisVMS.Proxy.exe", "proxy-build-version.txt", "0.223.0.500");
        WritePackage(Path.Combine("proxy", "linux-x64"), "LarisVMS.Proxy", "proxy-build-version.txt", "0.223.0.500");

        await NewRegistrar().RegisterAsync(Packages);

        var platforms = (await _db.NodeBuildVersions.ToListAsync()).Select(b => b.Platform).Order().ToList();
        Assert.Equal(["linux-x64", "proxy-linux-x64", "proxy-win-x64", "win-x64"], platforms);
        var linuxNode = await _db.NodeBuildVersions.SingleAsync(b => b.Platform == "linux-x64");
        Assert.NotNull(linuxNode.VisionFilePath);
        Assert.False(linuxNode.FilePath.EndsWith(".exe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SeedsEachPlatformsCudaProviderAndTheManifest()
    {
        var linuxSrc = Path.Combine(Packages, "cuda-provider", "linux-x64", "libonnxruntime_providers_cuda.so");
        Directory.CreateDirectory(Path.GetDirectoryName(linuxSrc)!);
        File.WriteAllText(linuxSrc, "cuda-linux");
        File.WriteAllText(linuxSrc + ".publisher.sig", "sig");
        File.WriteAllText(Path.Combine(Packages, "cuda-provider-manifest.json"), "{}");

        var registrar = NewRegistrar();
        await registrar.RegisterAsync(Packages);

        var dest = LarisVMS.Core.CudaProviderFiles.ServerPath(registrar.VisionNativeRoot, "linux-x64");
        Assert.Equal("cuda-linux", File.ReadAllText(dest));
        Assert.Equal("sig", File.ReadAllText(dest + ".publisher.sig"));
        Assert.True(File.Exists(Path.Combine(registrar.VisionNativeRoot, "cuda-provider-manifest.json")));
        Assert.False(File.Exists(LarisVMS.Core.CudaProviderFiles.ServerPath(registrar.VisionNativeRoot, "win-x64")));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
