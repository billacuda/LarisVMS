using System.Security.Cryptography;
using System.Text;
using LarisVMS.Core.Update;
using LarisVMS.NodeUpdater;

namespace LarisVMS.Tests;

/// <summary>The root helper that installs what an unprivileged Linux node or proxy staged
/// (<c>LarisVMS.NodeUpdater --linux-apply</c>), run against a temp tree with a fake restart. The mode
/// bits and systemctl are Linux-only and not exercised here.</summary>
public sealed class LinuxApplyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "larisvms-linuxapply-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _restarted = [];
    private readonly List<string> _log = [];

    private static readonly LinuxApply.Service Node = new("larisvms-node", "update", "LarisVMS.Node", HasVision: true, "linux-x64");

    private string UpdateDir => Path.Combine(_root, "update");
    private string InstallDir => Path.Combine(_root, "opt");

    public LinuxApplyTests()
    {
        Directory.CreateDirectory(UpdateDir);
        Directory.CreateDirectory(InstallDir);
        File.WriteAllText(Path.Combine(InstallDir, "LarisVMS.Node"), "old node");
        File.WriteAllText(Path.Combine(InstallDir, "LarisVMS.Vision.Service"), "old vision");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private int Run(LinuxApply.Service? service = null) =>
        LinuxApply.Run(service ?? Node, UpdateDir, InstallDir, (level, message) => _log.Add($"{level} {message}"),
            (unit, _) => { _restarted.Add(unit); return true; });

    private string Stage(string name, string content)
    {
        File.WriteAllText(Path.Combine(UpdateDir, name), content);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }

    [Fact]
    public void InstallsTheBinariesAndBackends_ThenRestarts()
    {
        var nodeSha = Stage("LarisVMS.Node", "new node");
        var visionSha = Stage("LarisVMS.Vision.Service", "new vision");
        var backends = Path.Combine(UpdateDir, BackendsBundle.StagedFolderName);
        Directory.CreateDirectory(Path.Combine(backends, "openvino"));
        File.WriteAllText(Path.Combine(backends, "openvino", "lib.so"), "ov");
        var stamp = BackendsBundle.ComputeStamp(backends);
        new LinuxUpdateRequest
        {
            Platform = "linux-x64", Version = "0.230.0.700", Sha256 = nodeSha, VisionSha256 = visionSha, BackendsStamp = stamp,
        }.WriteTo(UpdateDir);

        Assert.Equal(0, Run());

        Assert.Equal("new node", File.ReadAllText(Path.Combine(InstallDir, "LarisVMS.Node")));
        Assert.Equal("new vision", File.ReadAllText(Path.Combine(InstallDir, "LarisVMS.Vision.Service")));
        Assert.Equal(stamp, BackendsBundle.InstalledStamp(InstallDir));
        Assert.Equal(["larisvms-node"], _restarted);
        Assert.Empty(Directory.EnumerateFileSystemEntries(UpdateDir));
        Assert.False(Directory.Exists(Path.Combine(InstallDir, ".update-staging")));
    }

    [Fact]
    public void AChecksumMismatch_InstallsNothing_AndDoesNotRestart()
    {
        Stage("LarisVMS.Node", "tampered");
        new LinuxUpdateRequest { Platform = "linux-x64", Version = "0.230.0.700", Sha256 = new string('a', 64) }.WriteTo(UpdateDir);

        Assert.Equal(1, Run());

        Assert.Equal("old node", File.ReadAllText(Path.Combine(InstallDir, "LarisVMS.Node")));
        Assert.Empty(_restarted);
        Assert.False(File.Exists(Path.Combine(UpdateDir, LinuxUpdateRequest.FileName)));
    }

    [Fact]
    public void ARequestForAnotherPlatform_IsRefused()
    {
        var sha = Stage("LarisVMS.Node", "new node");
        new LinuxUpdateRequest { Platform = "win-x64", Version = "0.230.0.700", Sha256 = sha }.WriteTo(UpdateDir);

        Assert.Equal(1, Run());
        Assert.Equal("old node", File.ReadAllText(Path.Combine(InstallDir, "LarisVMS.Node")));
    }

    [Fact]
    public void BackendsThatDontMatchTheirStamp_AreLeftOut_ButTheBinaryInstalls()
    {
        var nodeSha = Stage("LarisVMS.Node", "new node");
        var backends = Path.Combine(UpdateDir, BackendsBundle.StagedFolderName);
        Directory.CreateDirectory(backends);
        File.WriteAllText(Path.Combine(backends, "lib.so"), "x");
        new LinuxUpdateRequest
        {
            Platform = "linux-x64", Version = "0.230.0.700", Sha256 = nodeSha, BackendsStamp = new string('f', 64),
        }.WriteTo(UpdateDir);

        Assert.Equal(0, Run());
        Assert.Equal("new node", File.ReadAllText(Path.Combine(InstallDir, "LarisVMS.Node")));
        Assert.False(Directory.Exists(Path.Combine(InstallDir, BackendsBundle.FolderName)));
    }

    [Fact]
    public void RestartOnly_JustRestarts()
    {
        new LinuxUpdateRequest { RestartOnly = true }.WriteTo(UpdateDir);

        Assert.Equal(0, Run());
        Assert.Equal(["larisvms-node"], _restarted);
        Assert.Equal("old node", File.ReadAllText(Path.Combine(InstallDir, "LarisVMS.Node")));
    }

    [Fact]
    public void NoRequest_DoesNothing()
    {
        Assert.Equal(0, Run());
        Assert.Empty(_restarted);
    }

    [Fact]
    public void OnlyTheNodeAndProxyUnitsAreAccepted()
    {
        Assert.NotNull(LinuxApply.ServiceFor("larisvms-node"));
        Assert.Equal("update-proxy", LinuxApply.ServiceFor("larisvms-proxy")!.UpdateFolder);
        Assert.Null(LinuxApply.ServiceFor("sshd"));
        Assert.Null(LinuxApply.ServiceFor(null));
    }
}
