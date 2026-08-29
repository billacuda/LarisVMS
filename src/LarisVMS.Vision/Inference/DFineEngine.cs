using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using YoloDotNet.Models;

namespace LarisVMS.Vision.Inference;

/// <summary>
/// D-FINE inference engine — runs directly on Microsoft.ML.OnnxRuntime (via OrtSessionFactory)
/// instead of going through YoloDotNet's own Yolo/IExecutionProvider wrapper the way the deleted
/// YoloEngine did.
///
/// Why bypass YoloDotNet entirely rather than adapt D-FINE to look like a model it already knows:
/// YoloDotNet does architecture-specific decoding internally (a closed set of YOLO-family metadata
/// strings, plus RT-DETR) — it has no generic ONNX path. Faking D-FINE's raw output into that shape
/// (the same graph-surgery trick the old YOLOv9 export pipeline used) is exactly the approach
/// already tried and abandoned for RT-DETR in this codebase, for the same reason: it requires
/// numerically reproducing a decoder this project can't read, and would need to be redone from
/// scratch for every future model family. A first-party decoder (DFineDecoder) is a one-time cost
/// that turns "support a new model family" into "add a Decode method" instead.
///
/// Preprocessing matches D-FINE's own published Hugging Face/Optimum preprocessor config exactly
/// (verified directly against a real model run — see DFineDecoder's own doc comment) for rescale
/// (1/255) and normalization (none — do_normalize is false, despite the config listing mean/std
/// values). Getting either wrong produces plausible-looking but numerically wrong detections, not an
/// obvious failure.
///
/// Resize/pad is no longer this class's own concern as of pass 1 of the detection/hardware-
/// acceleration overhaul: VisionSession's own ffmpeg filter chain now produces the captured frame
/// already at the exact network size <see cref="InferenceProfile"/> calls for (a plain stretch for
/// AspectMode.Stretch, matching HF's own do_pad=false config exactly; a real letterbox pad for
/// AspectMode.Letterbox) — so Preprocess below only ever packs pixels, never resizes.
/// </summary>
public sealed class DFineEngine : IDetectionEngine
{
    private readonly InferenceSession _session;
    private readonly InferenceProfile _profile;
    private readonly IReadOnlyList<string> _labels;
    private readonly ILogger<DFineEngine> _logger;
    private bool _loggedFirstInference;

    public DFineEngine(EngineOptions options, InferenceProfile profile, IReadOnlyList<string> labels, ILogger<DFineEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(labels);
        _profile = profile;
        _labels = labels;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var stopwatch = Stopwatch.StartNew();
        var sessionOptions = OrtSessionFactory.Create(options, logger);
        _session = new InferenceSession(options.ModelPath, sessionOptions);
        stopwatch.Stop();

        _logger.LogInformation("Loaded D-FINE model ({Classes} classes) from {Path} in {LoadMs} ms.",
            _labels.Count, options.ModelPath, stopwatch.ElapsedMilliseconds);
    }

    public double? LastInferenceMilliseconds { get; private set; }

    /// <summary>
    /// Runs detection on one frame. <paramref name="iou"/> is inert here — D-FINE is DETR-style
    /// (one query per object, see DFineDecoder's own doc comment) and never produces the duplicate
    /// boxes NMS exists to collapse. <paramref name="roi"/> is not yet supported (the deleted
    /// YoloEngine's own callers never passed one either — CameraDetectionPipeline always runs
    /// full-frame).
    /// </summary>
    public List<ObjectDetection> Detect(SKBitmap frame, double confidence = 0.25, double iou = 0.5, SKRectI? roi = null)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var stopwatch = Stopwatch.StartNew();

        var input = Preprocess(frame);
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("pixel_values", input) };

        using var outputs = _session.Run(inputs);
        var logitsTensor = outputs.First(o => o.Name == "logits").AsTensor<float>();
        var boxesTensor = outputs.First(o => o.Name == "pred_boxes").AsTensor<float>();

        var numQueries = logitsTensor.Dimensions[1];
        var numClasses = logitsTensor.Dimensions[2];

        var results = DFineDecoder.Decode(logitsTensor.ToArray(), boxesTensor.ToArray(), numQueries, numClasses,
            _labels, confidence, _profile);

        stopwatch.Stop();
        LastInferenceMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        if (!_loggedFirstInference)
        {
            _loggedFirstInference = true;
            // Same reasoning as the deleted YoloEngine's own first-inference log: there is no ONNX
            // Runtime API that reports which execution provider a session actually bound to, so
            // comparing this against a known-CPU timing is the only way to confirm a GPU provider
            // truly loaded rather than silently falling back.
            _logger.LogInformation(
                "First D-FINE inference: {Ms:F1} ms (includes provider warmup; compare against a " +
                "known-CPU run if this seems suspiciously slow for the configured accelerator).",
                LastInferenceMilliseconds);
        }

        return results;
    }

    /// <summary>Packs RGB (alpha dropped) into a [1,3,NetworkHeight,NetworkWidth] float32 NCHW buffer,
    /// scaled to 0-1 with no further normalization (matches D-FINE's own preprocessor exactly — see
    /// this class's own doc comment). No resize: as of pass 1, <paramref name="frame"/> is guaranteed
    /// to already be exactly _profile.NetworkWidth x NetworkHeight — VisionSession's own ffmpeg filter
    /// chain produces it pre-sized (stretched or letterboxed, per AspectMode), which is what let
    /// SKBitmap.Resize disappear from this hot path entirely (a real, measured CPU win beyond the fix
    /// already shipped in 0.158.0 — see this class's git history for that one).
    ///
    /// Reads raw BGRA8888 bytes via GetPixelSpan and writes straight into the tensor's own flat
    /// buffer, rather than SKBitmap.Pixels (a per-pixel SKColor[] conversion — a real, measured
    /// cost, confirmed once this shipped without it: CPU usage went up, not down, despite D-FINE's
    /// decode itself being lighter than YOLO's — see DFineDecoder's own doc comment for the other
    /// half of that fix) or the tensor's generic multi-dimensional indexer (recomputes
    /// strides per element; the flat Buffer.Span with precomputed channel offsets does not).</summary>
    private DenseTensor<float> Preprocess(SKBitmap frame)
    {
        if (frame.Width != _profile.NetworkWidth || frame.Height != _profile.NetworkHeight)
        {
            throw new InvalidOperationException(
                $"Captured frame is {frame.Width}x{frame.Height}, expected {_profile.NetworkWidth}x{_profile.NetworkHeight} " +
                "— VisionSession's ffmpeg filter chain and this engine's InferenceProfile have disagreed about the decode target.");
        }

        var pixelCount = _profile.NetworkWidth * _profile.NetworkHeight;
        var tensor = new DenseTensor<float>([1, 3, _profile.NetworkHeight, _profile.NetworkWidth]);
        var tensorSpan = tensor.Buffer.Span; // flat, row-major: [R plane][G plane][B plane], each H*W

        var pixelBytes = frame.GetPixelSpan(); // raw Bgra8888, row-major, 4 bytes/pixel: B,G,R,A
        var gPlane = pixelCount;
        var bPlane = pixelCount * 2;

        for (var i = 0; i < pixelCount; i++)
        {
            var byteOffset = i * 4;
            tensorSpan[bPlane + i] = pixelBytes[byteOffset] / 255f;     // B
            tensorSpan[gPlane + i] = pixelBytes[byteOffset + 1] / 255f; // G
            tensorSpan[i] = pixelBytes[byteOffset + 2] / 255f;          // R
        }

        return tensor;
    }

    public void Dispose() => _session.Dispose();
}
