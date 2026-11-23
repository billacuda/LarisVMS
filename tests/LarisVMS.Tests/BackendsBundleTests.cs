using System.IO.Compression;
using System.Text;
using LarisVMS.Core.Security;
using LarisVMS.Core.Update;
using LarisVMS.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Tests;

/// <summary>The native backend libraries shipped with node updates: the stamp the build script and
/// the node must agree on, the folder swap, the request a Linux node hands the root helper, and the
/// server registering the bundle.</summary>
public sealed class BackendsBundleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "larisvms-backends-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string WriteTree(string name, params (string Path, string Content)[] files)
    {
        var dir = Path.Combine(_root, name);
        foreach (var (relative, content) in files)
        {
            var path = Path.Combine(dir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        }
        return dir;
    }

    /// <summary>The same tree and value build-node.ps1's Get-BackendsStamp produced (checked in both
    /// PowerShell 7 and Windows PowerShell 5.1): the two implementations must never drift apart.</summary>
    [Fact]
    public void Stamp_MatchesTheBuildScript()
    {
        var dir = WriteTree("fixture",
            (".stamp", "zz\r\n"), ("B.txt", "c\r\n"), ("cuda/x.dll", "a\r\n"), ("openvino/sub/Y.so", "bb\r\n"));

        Assert.Equal("a84d68343634f0c57ea4ed27789a388a9bd36e88e8d757dab93415d65bcdebdf", BackendsBundle.ComputeStamp(dir));
    }

    [Fact]
    public void Stamp_IgnoresTheStampFile_AndSeesAnyChange()
    {
        var dir = WriteTree("tree", ("cuda/a.dll", "one"), ("openvino/b.dll", "two"));
        var before = BackendsBundle.ComputeStamp(dir);

        File.WriteAllText(Path.Combine(dir, BackendsBundle.StampFileName), before);
        Assert.Equal(before, BackendsBundle.ComputeStamp(dir));

        File.WriteAllText(Path.Combine(dir, "openvino", "b.dll"), "tampered");
        Assert.NotEqual(before, BackendsBundle.ComputeStamp(dir));
    }

    [Fact]
    public void SwapIn_ReplacesTheFolder_AndReadsTheNewStamp()
    {
        var install = Path.Combine(_root, "install");
        WriteTree(Path.Combine("install", BackendsBundle.FolderName), ("cuda/old.dll", "old"), (BackendsBundle.StampFileName, "aaaa"));
        var staged = WriteTree("staged", ("cuda/new.dll", "new"), (BackendsBundle.StampFileName, "BBBB"));

        Assert.True(BackendsBundle.TrySwapIn(staged, install, out var error), error);

        var current = Path.Combine(install, BackendsBundle.FolderName);
        Assert.True(File.Exists(Path.Combine(current, "cuda", "new.dll")));
        Assert.False(File.Exists(Path.Combine(current, "cuda", "old.dll")));
        Assert.False(Directory.Exists(Path.Combine(install, BackendsBundle.OldFolderName)));
        Assert.False(Directory.Exists(staged));
        Assert.Equal("bbbb", BackendsBundle.InstalledStamp(install));
    }

    [Fact]
    public void SwapIn_PutsTheOldFolderBack_WhenTheStagedOneIsMissing()
    {
        var install = Path.Combine(_root, "install");
        WriteTree(Path.Combine("install", BackendsBundle.FolderName), ("cuda/old.dll", "old"));

        Assert.False(BackendsBundle.TrySwapIn(Path.Combine(_root, "nothing-here"), install, out _));
        Assert.True(File.Exists(Path.Combine(install, BackendsBundle.FolderName, "cuda", "old.dll")));
    }

    [Fact]
    public void LinuxRequest_RoundTrips_AndRejectsJunk()
    {
        var request = new LinuxUpdateRequest
        {
            Platform = "linux-x64", Version = "0.230.0.700", Sha256 = new string('a', 64),
            VisionSha256 = new string('b', 64), BackendsStamp = new string('c', 64),
        };
        Directory.CreateDirectory(_root);
        request.WriteTo(_root);

        var path = Path.Combine(_root, LinuxUpdateRequest.FileName);
        Assert.Equal(request, LinuxUpdateRequest.FromJson(File.ReadAllText(path)));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Null(LinuxUpdateRequest.FromJson("{ not json"));
        Assert.True(LinuxUpdateRequest.FromJson("""{"RestartOnly":true}""")!.RestartOnly);
    }

    [Fact]
    public void BackendsPayload_CoversPlatformVersionAndStamp()
    {
        Assert.Equal("larisvms-backends:v1|linux-x64|0.230.0.700|abcd", UpdateSignature.BackendsPayload("linux-x64", "0.230.0.700", "ABCD"));
        Assert.NotEqual(UpdateSignature.BackendsPayload("win-x64", "1.0", "ab"), UpdateSignature.BackendsPayload("linux-x64", "1.0", "ab"));
    }

    [Fact]
    public async Task Registrar_StoresTheBundleWithTheNodeBuild()
    {
        var packages = Path.Combine(_root, "packages");
        var nodeDir = Path.Combine(packages, "node");
        Directory.CreateDirectory(nodeDir);
        File.WriteAllText(Path.Combine(nodeDir, "LarisVMS.Node.exe"), "node");
        File.WriteAllText(Path.Combine(nodeDir, "LarisVMS.Vision.Service.exe"), "vision");
        File.WriteAllText(Path.Combine(nodeDir, "node-build-version.txt"), "0.230.0.700");
        var tree = WriteTree("tree", ("cuda/a.dll", "one"));
        ZipFile.CreateFromDirectory(tree, Path.Combine(nodeDir, BackendsBundle.ZipFileName));
        var stamp = BackendsBundle.ComputeStamp(tree);
        File.WriteAllText(Path.Combine(nodeDir, BackendsBundle.StampTextFileName), stamp.ToUpperInvariant() + "\r\n");
        File.WriteAllText(Path.Combine(nodeDir, BackendsBundle.PublisherSignatureFileName), "sig");

        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await new BundledBuildRegistrar(db, NullLogger<BundledBuildRegistrar>.Instance)
        {
            NodeBuildsRoot = Path.Combine(_root, "node-builds"),
            VisionNativeRoot = Path.Combine(_root, "vision-native"),
        }.RegisterAsync(packages);

        var build = await db.NodeBuildVersions.SingleAsync();
        Assert.True(File.Exists(build.BackendsFilePath));
        Assert.Equal(stamp, build.BackendsStamp);
        Assert.Equal(64, build.BackendsSha256!.Length);
        Assert.Equal("sig", build.BackendsPublisherSignature);
    }

    [Fact]
    public async Task Registrar_SkipsTheBundle_WithoutAVisionService()
    {
        var packages = Path.Combine(_root, "packages");
        var nodeDir = Path.Combine(packages, "node");
        Directory.CreateDirectory(nodeDir);
        File.WriteAllText(Path.Combine(nodeDir, "LarisVMS.Node.exe"), "node");
        File.WriteAllText(Path.Combine(nodeDir, "node-build-version.txt"), "0.230.0.700");
        File.WriteAllText(Path.Combine(nodeDir, BackendsBundle.ZipFileName), "zip");
        File.WriteAllText(Path.Combine(nodeDir, BackendsBundle.StampTextFileName), new string('a', 64));

        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await new BundledBuildRegistrar(db, NullLogger<BundledBuildRegistrar>.Instance)
        {
            NodeBuildsRoot = Path.Combine(_root, "node-builds"),
            VisionNativeRoot = Path.Combine(_root, "vision-native"),
        }.RegisterAsync(packages);

        Assert.Null((await db.NodeBuildVersions.SingleAsync()).BackendsFilePath);
    }

    [Theory]
    [InlineData("1.23.2", true)]
    [InlineData("1.24.4", false)]
    [InlineData("1.24.0", false)]
    [InlineData("1.25.0", false)]
    [InlineData("garbage", false)]
    public void NativeOlderThanManaged_ComparesMajorMinor(string native, bool older) =>
        Assert.Equal(older, LarisVMS.Vision.Inference.OrtSessionFactory.NativeOlderThanManaged(native, new Version(1, 24, 4, 0)));
}
