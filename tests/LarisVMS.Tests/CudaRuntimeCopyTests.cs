using LarisVMS.Node;

namespace LarisVMS.Tests;

/// <summary>CudaProviderProvisioner.CopyMissingRuntime: putting back the CUDA/cuDNN libraries next to
/// the CUDA backend after a reinstall cleared them — the thing that silently dropped a node's AI
/// detection to DirectML.</summary>
public sealed class CudaRuntimeCopyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "laris-cudart-" + Guid.NewGuid().ToString("N"));

    public CudaRuntimeCopyTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Folder(string name, params string[] files)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        foreach (var f in files) File.WriteAllText(Path.Combine(dir, f), f);
        return dir;
    }

    [Fact]
    public void CuDnnIsCopiedWithItsCompanionLibraries()
    {
        var cuda = Folder("cuda", "onnxruntime.dll", "cudart64_12.dll", "cublas64_12.dll", "cublasLt64_12.dll", "cufft64_11.dll");
        var pip = Folder("pip-cudnn", "cudnn64_9.dll", "cudnn_ops64_9.dll", "cudnn_graph64_9.dll", "readme.txt");

        var copied = CudaProviderProvisioner.CopyMissingRuntime(cuda, [pip], _ => false, out var missing);

        Assert.Equal(["cudnn64_9.dll", "cudnn_graph64_9.dll", "cudnn_ops64_9.dll"], copied.Order(StringComparer.Ordinal));
        Assert.Empty(missing);
        Assert.True(File.Exists(Path.Combine(cuda, "cudnn_ops64_9.dll")));
        Assert.False(File.Exists(Path.Combine(cuda, "readme.txt")));
    }

    [Fact]
    public void LibrariesAlreadyLoadableFromPathAreLeftAlone()
    {
        var cuda = Folder("cuda");
        var toolkit = Folder("toolkit", "cudart64_12.dll", "cublas64_12.dll", "cublasLt64_12.dll", "cufft64_11.dll");
        var pip = Folder("pip", "cudnn64_9.dll");

        var copied = CudaProviderProvisioner.CopyMissingRuntime(cuda, [toolkit, pip],
            dll => !dll.StartsWith("cudnn", StringComparison.Ordinal), out var missing);

        Assert.Equal(["cudnn64_9.dll"], copied);
        Assert.Empty(missing);
    }

    [Fact]
    public void WhatCantBeFoundIsReported()
    {
        var cuda = Folder("cuda", "cudart64_12.dll", "cublas64_12.dll", "cublasLt64_12.dll", "cufft64_11.dll");

        var copied = CudaProviderProvisioner.CopyMissingRuntime(cuda, [Folder("empty")], _ => false, out var missing);

        Assert.Empty(copied);
        Assert.Equal(["cudnn64_9.dll"], missing);
    }
}
