namespace LarisVMS.Vision.Inference;

/// <summary>
/// Settings for one loaded model. Only the fields relevant to the compiled-in accelerator
/// (see <see cref="EngineFactory"/>) actually do anything -- e.g. <see cref="TensorRtPrecision"/>
/// is inert on a DirectML build.
///
/// Ported near-verbatim from aitest (g:\Projects\aitest\src\Aitest.Vision\Inference\EngineOptions.cs).
/// </summary>
public sealed class EngineOptions
{
    public required string ModelPath { get; init; }

    /// <summary>GPU device index for CUDA / DirectML. Ignored on CPU and OpenVINO builds.</summary>
    public int GpuId { get; init; }

    /// <summary>
    /// Directory containing cuDNN's DLLs (cudnn64_9.dll and friends), prepended to the process
    /// PATH before the CUDA execution provider is constructed.
    ///
    /// Only needed if cuDNN was not installed as a system-wide NVIDIA package -- for example, via
    /// `pip install nvidia-cudnn-cu12`, which places the DLLs under a Python environment's
    /// site-packages instead of anywhere the OS loader searches by default. Leave null if cuDNN
    /// is already discoverable (a system install, or already on PATH).
    ///
    /// Getting this wrong is a silent failure, not a loud one: if the CUDA execution provider
    /// can't find cuDNN, ONNX Runtime falls back to CPU without an error. EngineFactory logs
    /// whichever provider actually got requested so at least that half is visible; whether it
    /// truly bound is a matter of comparing inference latency against a known-CPU run.
    /// </summary>
    public string? CudnnPath { get; init; }

    /// <summary>
    /// Enables TensorRT on top of the CUDA execution provider. Off by default: the first engine
    /// build for a given model/precision/input-shape combination can take minutes, which is a bad
    /// surprise for a setting that looks like a simple toggle. Once built, engines are cached to
    /// <see cref="TensorRtEngineCachePath"/> and subsequent loads are fast.
    /// </summary>
    public bool EnableTensorRt { get; init; }

    public string TensorRtPrecision { get; init; } = "FP16";

    /// <summary>Required if <see cref="EnableTensorRt"/> is set; TensorRT needs somewhere to persist compiled engines.</summary>
    public string? TensorRtEngineCachePath { get; init; }

    /// <summary>
    /// Directory containing TensorRT's DLLs (nvinfer_10.dll and friends), prepended to the
    /// process PATH before the CUDA execution provider is constructed -- same idea as
    /// <see cref="CudnnPath"/>, since TensorRT ships as a standalone SDK download (not a system
    /// installer) and its lib folder is never somewhere the OS loader searches by default. Leave
    /// null if it's already on PATH.
    /// </summary>
    public string? TensorRtLibPath { get; init; }

    /// <summary>OpenVINO target device, e.g. "GPU" for an Intel iGPU or "CPU". Ignored outside an OpenVINO build.</summary>
    public string OpenVinoDeviceType { get; init; } = "GPU";

    /// <summary>Pass 4a: when set, an nv12 -&gt; normalized-RGB-tensor preprocessing head is merged
    /// into the model at load (<see cref="OnnxPreprocessHead"/>), so colour conversion + normalize
    /// run on the accelerator instead of a CPU pixel loop. The continuous-detection frame then flows
    /// as raw nv12 bytes rather than a decoded BGRA <c>SKBitmap</c>. Vendor-neutral — the head runs
    /// on whatever execution provider this build uses. Off by default.</summary>
    public bool GpuPreprocessing { get; init; }
}
