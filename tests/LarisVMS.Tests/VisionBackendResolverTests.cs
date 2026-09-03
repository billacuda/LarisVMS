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

    public VisionBackendResolverTests() => VisionBackendResolver.BackendsRootOverride = _root;

    public void Dispose()
    {
        VisionBackendResolver.BackendsRootOverride = null;
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void CreateBackendFolder(string name, bool withCudaProvider = true)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "onnxruntime.dll"), "");
        if (name == "cuda" && withCudaProvider)
            File.WriteAllText(Path.Combine(dir, "onnxruntime_providers_cuda.dll"), "");
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

    [Fact]
    public void IntelRequestResolvesToDirectMlWhenThatBackendIsPresent()
    {
        CreateBackendFolder("directml");

        VisionBackendResolver.Resolve("Intel", null, NullLogger.Instance);

        Assert.Equal(VisionBackend.DirectMl, VisionBackendResolver.Current.ResolvedBackend);
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
    public void ExplicitBackendOverrideWins()
    {
        CreateBackendFolder("directml");
        CreateBackendFolder("cuda");

        VisionBackendResolver.Resolve("Nvidia", "directml", NullLogger.Instance);

        Assert.Equal(VisionBackend.DirectMl, VisionBackendResolver.Current.ResolvedBackend);
    }
}
