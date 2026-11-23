using LarisVMS.Vision.Inference;
using Microsoft.Extensions.Logging.Abstractions;

namespace LarisVMS.Tests;

/// <summary>
/// Covers the deterministic part of backend resolution — the fallback chain and the operator notes
/// — by pointing <see cref="VisionBackendResolver.BackendsRootOverride"/> at a temp directory whose
/// backend subfolders we create or omit. The CUDA prerequisite probe (cudart/cuDNN on PATH) and the
/// actual DLL-loader wiring need a real machine and are exercised by the deploy verification, not here.
/// </summary>
public sealed class VisionBackendResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "larisvms-backends-" + Guid.NewGuid().ToString("N"));

    public VisionBackendResolverTests()
    {
        VisionBackendResolver.BackendsRootOverride = _root;
        // The test machine's own GPU driver must not decide these tests.
        VisionBackendResolver.DriverVersionProbe = () => null;
    }

    public void Dispose()
    {
        VisionBackendResolver.BackendsRootOverride = null;
        VisionBackendResolver.DriverVersionProbe = CudaRequirements.InstalledDriverVersion;
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void CreateBackendFolder(string name, bool withCudaProvider = true)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, LarisVMS.Core.NativeLibraryNames.OnnxRuntime), "");
        if (name == "cuda" && withCudaProvider)
            File.WriteAllText(Path.Combine(dir, LarisVMS.Core.NativeLibraryNames.OnnxRuntimeCudaProvider), "");
    }

    [Fact]
    public void CpuRequestResolvesToCpuWithNoNotes()
    {
        VisionBackendResolver.Resolve("Cpu", null, NullLogger.Instance);

        var report = VisionBackendResolver.Current;
        Assert.Equal(VisionBackend.Cpu, report.ResolvedBackend);
        Assert.Equal("CPU", report.EffectiveExecutionProvider);
        Assert.False(report.NeedsAttention);
    }

    /// <summary>The OpenVINO folder holds the plugin library, not an onnxruntime build — it loads on
    /// top of the CPU onnxruntime at the app root.</summary>
    private void CreateOpenVinoFolder()
    {
        var dir = Path.Combine(_root, "openvino");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, VisionBackendResolver.OpenVinoPluginFileName), "");
    }

    [Theory]
    [InlineData("Intel")]
    [InlineData("OpenVino")]
    public void IntelAndOpenVinoRequestsResolveToTheOpenVinoPlugin(string requested)
    {
        CreateOpenVinoFolder();
        CreateBackendFolder("directml");

        VisionBackendResolver.Resolve(requested, null, NullLogger.Instance);

        var report = VisionBackendResolver.Current;
        Assert.Equal(VisionBackend.OpenVino, report.ResolvedBackend);
        Assert.Equal("OpenVINO", report.EffectiveExecutionProvider);
        Assert.False(report.NeedsAttention);
    }

    [Fact]
    public void IntelFallsBackToDirectMlWhenTheOpenVinoPluginIsMissing()
    {
        CreateBackendFolder("directml");

        VisionBackendResolver.Resolve("Intel", null, NullLogger.Instance);

        var report = VisionBackendResolver.Current;
        Assert.Equal(VisionBackend.DirectMl, report.ResolvedBackend);
        Assert.Contains(report.Notes, n => n.Contains("OpenVINO", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IntelFallsBackToCpuWhenNeitherOpenVinoNorDirectMlIsPresent()
    {
        // A Linux node: no DirectML folder exists there at all.
        VisionBackendResolver.Resolve("Intel", null, NullLogger.Instance);

        Assert.Equal(VisionBackend.Cpu, VisionBackendResolver.Current.ResolvedBackend);
    }

    [Fact]
    public void AmdStaysOnDirectMlEvenWhenTheOpenVinoPluginIsPresent()
    {
        CreateOpenVinoFolder();
        CreateBackendFolder("directml");

        VisionBackendResolver.Resolve("Amd", null, NullLogger.Instance);

        Assert.Equal(VisionBackend.DirectMl, VisionBackendResolver.Current.ResolvedBackend);
    }

    [Theory]
    [InlineData(null, "GPU", 0)]
    [InlineData("", "GPU", 0)]
    [InlineData("GPU", "GPU", 0)]
    [InlineData("gpu.1", "GPU", 1)]
    [InlineData("NPU", "NPU", 0)]
    [InlineData("CPU", "CPU", 0)]
    [InlineData("GPU.x", "GPU", 0)]
    public void ParsesOpenVinoDeviceSettings(string? setting, string type, int index)
    {
        Assert.Equal((type, index), OrtSessionFactory.ParseOpenVinoDevice(setting));
    }

    [Fact]
    public void NvidiaFallsBackToDirectMlAndFlagsTheNodeWhenTheCudaToolkitIsMissing()
    {
        // No cuda folder at all -> can't even try CUDA; directml is present.
        CreateBackendFolder("directml");

        VisionBackendResolver.Resolve("Nvidia", null, NullLogger.Instance);

        var report = VisionBackendResolver.Current;
        Assert.Equal(VisionBackend.DirectMl, report.ResolvedBackend);
        Assert.True(report.NeedsAttention);
        Assert.Contains(report.Notes, n => n.Contains("CUDA", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FallsAllTheWayToCpuWhenNoGpuBackendFolderExists()
    {
        VisionBackendResolver.Resolve("Nvidia", null, NullLogger.Instance);

        Assert.Equal(VisionBackend.Cpu, VisionBackendResolver.Current.ResolvedBackend);
    }

    [Fact]
    public void NvidiaFallsBackToDirectMlWhenTheCudaProviderLibraryHasNotBeenDownloadedYet()
    {
        CreateBackendFolder("cuda", withCudaProvider: false);
        CreateBackendFolder("directml");

        VisionBackendResolver.Resolve("Nvidia", null, NullLogger.Instance);

        var report = VisionBackendResolver.Current;
        Assert.Equal(VisionBackend.DirectMl, report.ResolvedBackend);
        Assert.Contains(report.Notes, n => n.Contains("provider library", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnNvidiaDriverTooOldForThisCudaFallsBackAndSaysSo()
    {
        CreateBackendFolder("cuda");
        CreateBackendFolder("directml");
        VisionBackendResolver.DriverVersionProbe = () => new Version(535, 104, 5);

        VisionBackendResolver.Resolve("Nvidia", null, NullLogger.Instance);

        var report = VisionBackendResolver.Current;
        Assert.Equal(VisionBackend.DirectMl, report.ResolvedBackend);
        Assert.Contains(report.Notes, n => n.Contains("535.104.5", StringComparison.Ordinal) && n.Contains("driver", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExplicitBackendOverrideWins()
    {
        CreateBackendFolder("directml");
        CreateBackendFolder("cuda");

        VisionBackendResolver.Resolve("Nvidia", "directml", NullLogger.Instance);

        Assert.Equal(VisionBackend.DirectMl, VisionBackendResolver.Current.ResolvedBackend);
    }
}
