using LarisVMS.Vision.Inference;

namespace LarisVMS.Tests;

/// <summary>The NVIDIA driver check reads the version two ways (Linux kernel module, Windows
/// nvidia-smi) and must never block a GPU whose version it couldn't read.</summary>
public sealed class CudaRequirementsTests
{
    [Fact]
    public void ParsesTheLinuxKernelModuleVersion()
    {
        const string text = "NVRM version: NVIDIA UNIX x86_64 Kernel Module  570.86.15  Thu Jan 23 21:42:29 UTC 2026\nGCC version:  gcc version 13.3.0\n";
        Assert.Equal(new Version(570, 86, 15), CudaRequirements.ParseProcVersion(text));
    }

    [Fact]
    public void ParsesNvidiaSmiAndUsesTheFirstGpu() =>
        Assert.Equal(new Version(572, 16), CudaRequirements.ParseNvidiaSmi("572.16\r\n572.16\r\n"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NVIDIA-SMI has failed")]
    public void UnreadableOutputIsUnknown(string? output) => Assert.Null(CudaRequirements.ParseNvidiaSmi(output));

    [Fact]
    public void OnlyAKnownOldDriverIsFlagged()
    {
        Assert.NotNull(CudaRequirements.DriverTooOldNote(new Version(560, 94), CudaRequirements.MinDriverWindows));
        Assert.Null(CudaRequirements.DriverTooOldNote(new Version(570, 65), CudaRequirements.MinDriverWindows));
        Assert.Null(CudaRequirements.DriverTooOldNote(new Version(580, 1), CudaRequirements.MinDriverLinux));
        Assert.Null(CudaRequirements.DriverTooOldNote(null, CudaRequirements.MinDriverLinux));
    }

    [Fact]
    public void MinimumsMatchCuda128()
    {
        Assert.Equal(new Version(570, 26), CudaRequirements.MinDriverLinux);
        Assert.Equal(new Version(570, 65), CudaRequirements.MinDriverWindows);
    }
}
