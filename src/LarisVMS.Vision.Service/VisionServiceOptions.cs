using LarisVMS.Vision.Inference;

namespace LarisVMS.Vision.Service;

/// <summary>
/// This process's own local configuration (appsettings.json / environment variables /
/// command-line) — deliberately limited to facts about *this specific machine*: which model file,
/// which GPU device, where cuDNN/TensorRT live. Everything else needed to watch a camera (RTSP URI,
/// decode resolution, detection thresholds, where to report back) arrives per-camera in
/// VisionStartCameraRequest instead — see that record's own doc comment for why the split lands
/// there.
/// </summary>
public sealed class VisionServiceOptions
{
    /// <summary>Loopback-only port this process's control API listens on. NodeWorker discovers
    /// this the same way any other node-local setting is configured — not resolved from the
    /// central server, since this is purely an implementation detail of how Node and Vision Service
    /// talk to each other on the same machine.</summary>
    public int Port { get; set; } = 5990;

    /// <summary>Path to the .onnx model file, absolute or relative to the application directory. One
    /// model serves every camera this instance watches — see CameraDetectionPipeline's own doc
    /// comment for why each camera still gets its own loaded Yolo instance despite sharing one file.
    ///
    /// Left unset, the default below does NOT have to exist: CameraPipelineManager.ResolveModelPath
    /// falls back to whatever .onnx was actually bundled in that directory, which is the normal case
    /// (build-node.ps1 bundles the exporter's own file names, never "model.onnx"). Set this only to
    /// pin a specific model when more than one is bundled.</summary>
    public string ModelPath { get; set; } = "models/model.onnx";

    /// <summary>Resolved ffmpeg executable for VisionSession's own capture process. Defaults to
    /// whatever LarisVMS.Media.FfmpegPathResolver resolves — see Program.cs.</summary>
    public string? FfmpegPath { get; set; }

    /// <summary>GPU device index for CUDA / DirectML. Ignored on CPU and OpenVINO builds — see
    /// Inference.EngineOptions.GpuId's own doc comment.</summary>
    public int GpuId { get; set; }

    /// <summary>Directory containing cuDNN's DLLs, if not already on PATH — see
    /// Inference.EngineOptions.CudnnPath's own doc comment. Irrelevant on non-CUDA builds.</summary>
    public string? CudnnPath { get; set; }

    public bool EnableTensorRt { get; set; }
    public string TensorRtPrecision { get; set; } = "FP16";

    /// <summary>Optional — defaults to %ProgramData%\LarisVMS\trt-cache. See
    /// Inference.EngineOptions.TensorRtEngineCachePath.</summary>
    public string? TensorRtEngineCachePath { get; set; }
    public string? TensorRtLibPath { get; set; }

    /// <summary>Caps the VRAM TensorRT's builder may take while compiling — see
    /// Inference.EngineOptions.TensorRtMaxWorkspaceBytes for why an unbounded builder is a problem on
    /// a node that also decodes on the GPU.</summary>
    public long TensorRtMaxWorkspaceBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Null keeps ONNX Runtime's default (3). Lower builds faster, runs slightly slower —
    /// a per-node A/B, not a default to change on reasoning.</summary>
    public int? TensorRtBuilderOptimizationLevel { get; set; }

    /// <summary>OpenVINO target device, e.g. "GPU" for an Intel iGPU or "CPU". Ignored unless the
    /// OpenVINO backend is selected.</summary>
    public string OpenVinoDeviceType { get; set; } = "GPU";

    /// <summary>The <c>AiAccelerator</c> the node resolved for this machine's hardware ("Nvidia",
    /// "Intel", "Amd", "Cpu"), passed through by VisionServiceSupervisor as <c>Vision__PreferredAccelerator</c>.
    /// <see cref="VisionBackendResolver"/> maps it to one of the bundled native ONNX Runtime backends
    /// at startup. "Auto" (the default when nothing is passed) resolves to CPU.</summary>
    public string PreferredAccelerator { get; set; } = "Auto";

    /// <summary>Optional hard override of the ONNX Runtime backend regardless of detected hardware:
    /// "cuda", "directml", "openvino" or "cpu". The only way to select OpenVINO (there is no
    /// hardware auto-path to it, since DirectML covers Intel GPUs without an extra driver dependency).</summary>
    public string? Backend { get; set; }
}
