namespace LarisVMS.Vision.Models;

/// <summary>Pixel normalization applied while packing an image into the input tensor:
/// <c>value = (channel * scale - mean) / std</c>, per channel. The default (<c>scale = 1/255</c>,
/// <c>mean = 0</c>, <c>std = 1</c>) matches a plain Ultralytics export. Ported from SideGlance's
/// <c>Config.NormalizeSpec</c>.</summary>
public sealed record NormalizeSpec
{
    public double Scale { get; init; } = 1.0 / 255.0;
    public IReadOnlyList<double> Mean { get; init; } = [0, 0, 0];
    public IReadOnlyList<double> Std { get; init; } = [1, 1, 1];
}

/// <summary>Batch inference for one descriptor-driven model. Ported from SideGlance's
/// <c>Config.BatchSpec</c> — mirrors <see cref="LarisVMS.Vision.Inference.EngineOptions.BatchSize"/>'s
/// own meaning (fixed for the pipeline's lifetime, not a per-call variable).</summary>
public sealed record BatchSpec
{
    public bool Enabled { get; init; }

    /// <summary>Caps one batched pass and drives the TensorRT shape profile.</summary>
    public int MaxBatchSize { get; init; } = 8;
}

/// <summary>TensorRT settings for one descriptor-driven model — CUDA backend only; ignored (with a
/// warning) on DirectML / OpenVINO / CPU. Ported from SideGlance's <c>Config.TensorRtSpec</c>.</summary>
public sealed record TensorRtSpec
{
    public bool Enabled { get; init; }

    /// <summary><c>"FP16"</c> or <c>"FP32"</c>.</summary>
    public string Precision { get; init; } = "FP16";

    /// <summary>Upper bound on builder workspace VRAM. Default 2048 MiB.</summary>
    public int WorkspaceMB { get; init; } = 2048;

    /// <summary><c>trt_builder_optimization_level</c>, or null for ONNX Runtime's default.</summary>
    public int? BuilderOptimizationLevel { get; init; }

    /// <summary>Forces LayerNorm subgraphs to FP32 under <c>trt_fp16_enable</c> — needed by
    /// transformer detectors (DETR-family, e.g. D-FINE) whose LayerNorm overflows FP16. Harmless for
    /// CNN YOLOs. Same knob as <see cref="LarisVMS.Vision.Inference.EngineOptions.TensorRtLayerNormFp32Fallback"/>.</summary>
    public bool LayerNormFp32Fallback { get; init; }
}
