using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using YoloDotNet;
using YoloDotNet.Enums;
using YoloDotNet.Models;

namespace LarisVMS.Vision.Inference;

/// <summary>
/// Wraps a single loaded <see cref="Yolo"/> instance for object detection.
///
/// YoloDotNet's <see cref="Yolo"/> holds pinned buffers reused across calls and is not
/// thread-safe, so this -- like <see cref="Yolo"/> itself -- is meant to be owned by exactly one
/// pipeline (one camera's inference loop), not shared across threads.
///
/// Ported near-verbatim from aitest (g:\Projects\aitest\src\Aitest.Vision\Inference\YoloEngine.cs).
/// </summary>
public sealed class YoloEngine : IDisposable
{
    private readonly Yolo _yolo;
    private readonly ILogger<YoloEngine> _logger;
    private bool _loggedFirstInference;

    public YoloEngine(EngineOptions options, ILogger<YoloEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var stopwatch = Stopwatch.StartNew();

        _yolo = new Yolo(new YoloOptions
        {
            ExecutionProvider = EngineFactory.Create(options, logger),
            ImageResize = ImageResize.Proportional,
        });

        stopwatch.Stop();

        _logger.LogInformation("Loaded {ModelInfo} from {Path} in {LoadMs} ms.",
            _yolo.ModelInfo, options.ModelPath, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>Duration of the most recent <see cref="Detect"/> call, for diagnostics.</summary>
    public double? LastInferenceMilliseconds { get; private set; }

    public string ModelInfo => _yolo.ModelInfo;

    /// <summary>
    /// Runs detection on one frame. <paramref name="iou"/> controls YoloDotNet's own
    /// non-maximum suppression -- it is meaningful for every model this engine loads (YOLO9's
    /// raw output requires it), unlike some end-to-end-exported architectures where NMS is baked
    /// into the graph and the parameter would be inert.
    /// </summary>
    public List<ObjectDetection> Detect(SKBitmap frame, double confidence = 0.25, double iou = 0.5, SKRectI? roi = null)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var stopwatch = Stopwatch.StartNew();
        var results = _yolo.RunObjectDetection(frame, confidence, iou, roi);
        stopwatch.Stop();

        LastInferenceMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        if (!_loggedFirstInference)
        {
            _loggedFirstInference = true;

            // The first call includes provider warmup (CUDA context creation, kernel
            // compilation, etc.), so it is not representative of steady-state latency -- but its
            // absolute value is the cheapest signal available that the requested provider
            // actually bound rather than silently falling back to CPU. There is no ONNX Runtime
            // API that reports which execution provider a session ended up using; comparing
            // timing across provider builds (see the plan's provider-matrix verification step) is
            // the only way to confirm this with certainty.
            _logger.LogInformation(
                "First inference: {Ms:F1} ms (includes provider warmup; compare against a known-CPU " +
                "run if this seems suspiciously slow for the configured accelerator).",
                LastInferenceMilliseconds);
        }

        return results;
    }

    public void Dispose() => _yolo.Dispose();
}
