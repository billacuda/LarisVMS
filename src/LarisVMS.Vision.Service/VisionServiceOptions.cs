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

    /// <summary>Path to the .onnx model file, absolute or relative to the content root. One model
    /// serves every camera this instance watches — see CameraDetectionPipeline's own doc comment
    /// for why each camera still gets its own loaded Yolo instance despite sharing one file.</summary>
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
    public string? TensorRtEngineCachePath { get; set; }
    public string? TensorRtLibPath { get; set; }

    /// <summary>OpenVINO target device, e.g. "GPU" for an Intel iGPU or "CPU". Ignored outside an
    /// OpenVINO build.</summary>
    public string OpenVinoDeviceType { get; set; } = "GPU";
}
