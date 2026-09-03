namespace LarisVMS.Vision.Inference;

/// <summary>
/// Which native ONNX Runtime build (and therefore execution-provider family) this Vision Service
/// process is running against. Chosen once at startup by <see cref="VisionBackendResolver"/> from
/// the node's detected hardware — the successor to the old compile-time <c>ACCEL_*</c> symbol.
///
/// Every node package now ships all four native runtimes side by side under
/// <c>onnxruntime-backends/&lt;name&gt;/</c> (build-node.ps1 stages them); the resolver picks one
/// folder, points the OS loader at it, and falls back down the list when a backend's prerequisites
/// are missing.
/// </summary>
public enum VisionBackend
{
    /// <summary>Pure CPU — always available, the universal fallback.</summary>
    Cpu = 0,

    /// <summary>NVIDIA CUDA (optionally TensorRT). Needs the CUDA Toolkit 12.x runtime and cuDNN 9.x
    /// on the machine — neither is bundled.</summary>
    Cuda = 1,

    /// <summary>DirectML — any Direct3D 12 GPU (Intel, AMD, NVIDIA). Ships <c>DirectML.dll</c> in the
    /// package; needs nothing else installed on a current Windows build.</summary>
    DirectMl = 2,

    /// <summary>Intel OpenVINO — Intel iGPU/CPU. Opt-in only (<c>Vision:Backend=OpenVino</c>); needs a
    /// current Intel GPU driver.</summary>
    OpenVino = 3,
}
