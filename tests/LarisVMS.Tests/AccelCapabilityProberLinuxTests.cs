using LarisVMS.Core.Enums;
using LarisVMS.Node;

namespace LarisVMS.Tests;

/// <summary>Linux GPU detection reads PCI vendor IDs from /sys/class/drm; without it Auto finds no
/// accelerator and a Linux node never starts the Vision service. Runs against a fake tree.</summary>
public sealed class AccelCapabilityProberLinuxTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "larisvms-drm-" + Guid.NewGuid().ToString("N"));
    private string Drm => Path.Combine(_root, "drm");
    private string NvidiaProc => Path.Combine(_root, "proc-nvidia");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void AddCard(string name, string? vendor)
    {
        var device = Path.Combine(Drm, name, "device");
        Directory.CreateDirectory(device);
        if (vendor is not null) File.WriteAllText(Path.Combine(device, "vendor"), vendor + "\n");
    }

    [Fact]
    public void ReadsEachCardsVendorAndSkipsConnectors()
    {
        AddCard("card0", "0x8086");
        AddCard("card1", "0x10de");
        AddCard("card0-HDMI-A-1", "0x1002");
        AddCard("renderD128", "0x1002");

        var vendors = AccelCapabilityProber.QueryLinuxGpuVendors(Drm, NvidiaProc);

        Assert.Equal(["Intel", "NVIDIA"], vendors.Order());
        Assert.Equal([AiAccelerator.Nvidia, AiAccelerator.Intel], AccelCapabilityProber.ParseAccelerators(vendors));
    }

    [Fact]
    public void AnAmdCardIsDetected()
    {
        AddCard("card0", "0x1002");

        Assert.Equal([AiAccelerator.Amd], AccelCapabilityProber.ParseAccelerators(AccelCapabilityProber.QueryLinuxGpuVendors(Drm, NvidiaProc)));
    }

    [Fact]
    public void NvidiaDriverWithoutADrmDeviceStillCounts()
    {
        Directory.CreateDirectory(NvidiaProc);

        Assert.Equal(["NVIDIA"], AccelCapabilityProber.QueryLinuxGpuVendors(Drm, NvidiaProc));
    }

    [Fact]
    public void UnknownVendorsMissingFilesAndNoDrmFolderGiveNothing()
    {
        Assert.Empty(AccelCapabilityProber.QueryLinuxGpuVendors(Drm, NvidiaProc));
        AddCard("card0", "0x1af4");
        AddCard("card1", null);
        Assert.Empty(AccelCapabilityProber.QueryLinuxGpuVendors(Drm, NvidiaProc));
    }
}
