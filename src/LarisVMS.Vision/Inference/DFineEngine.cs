using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
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
public sealed class DFineEngine : IDetectionEngine, IBatchDetectionEngine
{
    // The one session for this camera. When GpuPreprocessing is on it has the nv12 -> normalized
    // tensor head merged in (OnnxPreprocessHead) and its input is "nv12"; otherwise its input is the
    // model's own "pixel_values" and the CPU packs it. Both the continuous Detect and the high-res
    // DetectBatch (pass 4b) feed 640x640 nv12 through this same session.
    private readonly InferenceSession _session;

    private readonly bool _gpuPreprocessing;
    private readonly InferenceProfile _profile;
    private readonly IReadOnlyList<string> _labels;
    private readonly ILogger<DFineEngine> _logger;
    private bool _loggedFirstInference;

    // Input tensors reused across calls rather than allocated per frame — at 640x640 each is 4.92 MB
    // (H*W*3 floats), a Large Object Heap allocation every inference, which on a busy node forces
    // several gen2 collections per second. ONNX Runtime has copied the tensor to the device by the
    // time Run returns, so the next call is free to overwrite it.
    //
    // Two of them, not one, because this engine has two genuinely concurrent entry points:
    // CameraDetectionPipeline calls Detect from its single inference loop and DetectBatch from
    // HighResReDetectionLoopAsync, which is a separate task running alongside it. Each preprocessor
    // has exactly one of those callers (Preprocess <- Detect, PreprocessNv12 <- DetectBatch), so a
    // buffer per preprocessor keeps each one single-threaded. Sharing a single tensor between them
    // would be a race. Allocated lazily so the path a given deployment doesn't use costs nothing.
    private DenseTensor<float>? _detectTensor;
    private DenseTensor<float>? _batchTensor;

    public DFineEngine(EngineOptions options, InferenceProfile profile, IReadOnlyList<string> labels, ILogger<DFineEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(labels);
        _profile = profile;
        _labels = labels;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _gpuPreprocessing = options.GpuPreprocessing;

        var stopwatch = Stopwatch.StartNew();
        if (_gpuPreprocessing)
        {
            var merged = OnnxPreprocessHead.Merge(File.ReadAllBytes(options.ModelPath),
                profile.NetworkWidth, profile.NetworkHeight);
            _session = new InferenceSession(merged, OrtSessionFactory.Create(options, logger));
        }
        else
        {
            _session = new InferenceSession(options.ModelPath, OrtSessionFactory.Create(options, logger));
        }
        stopwatch.Stop();

        _logger.LogInformation(
            "Loaded D-FINE model ({Classes} classes) from {Path} in {LoadMs} ms (GPU preprocessing: {Gpu}).",
            _labels.Count, options.ModelPath, stopwatch.ElapsedMilliseconds, _gpuPreprocessing);
    }

    public double? LastInferenceMilliseconds { get; private set; }

    /// <summary>
    /// Runs detection on one frame. <paramref name="iou"/> is inert here — D-FINE is DETR-style
    /// (one query per object, see DFineDecoder's own doc comment) and never produces the duplicate
    /// boxes NMS exists to collapse. <paramref name="roi"/> is not yet supported (the deleted
    /// YoloEngine's own callers never passed one either — CameraDetectionPipeline always runs
    /// full-frame).
    /// </summary>
    public List<ObjectDetection> Detect(byte[] frame, double confidence = 0.25, double iou = 0.5)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var stopwatch = Stopwatch.StartNew();

        List<NamedOnnxValue> inputs;
        if (_gpuPreprocessing)
        {
            var expected = _profile.NetworkWidth * _profile.NetworkHeight * 3 / 2;
            if (frame.Length != expected)
                throw new InvalidOperationException(
                    $"nv12 frame is {frame.Length} bytes, expected {expected} for {_profile.NetworkWidth}x{_profile.NetworkHeight}.");
            // No copy: ORT does its own single host->device copy of this host tensor.
            var nv12 = new DenseTensor<byte>(frame, [1, _profile.NetworkHeight * 3 / 2, _profile.NetworkWidth]);
            inputs = [NamedOnnxValue.CreateFromTensor("nv12", nv12)];
        }
        else
        {
            inputs = [NamedOnnxValue.CreateFromTensor("pixel_values", Preprocess(frame))];
        }

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

    /// <summary>Pass 4b: high-res re-detection — the pre-letterboxed whole-frame nv12 plus every
    /// native-scale tile nv12, each run through its own <see cref="_session"/> forward pass (≤5, and
    /// only on a trigger, so per-image beats the complexity of a dynamic-batch graph). Each image
    /// carries its own <see cref="InferenceProfile"/> for <see cref="DFineDecoder.Decode"/> —
    /// a real letterbox profile for the whole frame, an identity Stretch profile for a tile.</summary>
    public List<List<ObjectDetection>> DetectBatch(IReadOnlyList<(byte[] Nv12, InferenceProfile Profile)> images, double confidence)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 0) return [];

        var stopwatch = Stopwatch.StartNew();
        var expectedNv12 = _profile.NetworkWidth * _profile.NetworkHeight * 3 / 2;
        var results = new List<List<ObjectDetection>>(images.Count);

        foreach (var (nv12, imageProfile) in images)
        {
            if (nv12.Length != expectedNv12)
                throw new InvalidOperationException(
                    $"DetectBatch image is {nv12.Length} bytes, expected {expectedNv12} ({_profile.NetworkWidth}x{_profile.NetworkHeight} nv12).");

            List<NamedOnnxValue> inputs = _gpuPreprocessing
                ? [NamedOnnxValue.CreateFromTensor("nv12",
                    new DenseTensor<byte>(nv12, [1, _profile.NetworkHeight * 3 / 2, _profile.NetworkWidth]))]
                : [NamedOnnxValue.CreateFromTensor("pixel_values", PreprocessNv12(nv12))];

            using var outputs = _session.Run(inputs);
            var logits = outputs.First(o => o.Name == "logits").AsTensor<float>();
            var boxes = outputs.First(o => o.Name == "pred_boxes").AsTensor<float>();
            var numQueries = logits.Dimensions[1];
            var numClasses = logits.Dimensions[2];
            results.Add(DFineDecoder.Decode(logits.ToArray(), boxes.ToArray(), numQueries, numClasses,
                _labels, confidence, imageProfile));
        }

        stopwatch.Stop();
        LastInferenceMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        return results;
    }

    /// <summary>CPU fallback (GpuPreprocessing off): one 640x640 packed-nv12 frame → a
    /// [1,3,H,W] float32 RGB tensor, /255, BT.601 limited range. Only used for the high-res tiles on
    /// a non-CUDA node — opt-in and rare.</summary>
    private DenseTensor<float> PreprocessNv12(byte[] nv12)
    {
        int w = _profile.NetworkWidth, h = _profile.NetworkHeight;
        var pixelCount = w * h;
        _batchTensor ??= new DenseTensor<float>([1, 3, h, w]);
        var t = _batchTensor.Buffer.Span;
        var gPlane = pixelCount;
        var bPlane = pixelCount * 2;

        for (var y = 0; y < h; y++)
        {
            var cyRow = pixelCount + (y / 2) * w;
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                var cxByte = (x / 2) * 2;
                var (r, g, b) = Bt601Limited.ToRgb(nv12[i], nv12[cyRow + cxByte], nv12[cyRow + cxByte + 1]);
                t[i] = r / 255f;
                t[gPlane + i] = g / 255f;
                t[bPlane + i] = b / 255f;
            }
        }

        return _batchTensor;
    }

    /// <summary>CPU fallback preprocessing (GpuPreprocessing off): packs a raw BGRA8888 frame buffer
    /// into a [1,3,NetworkHeight,NetworkWidth] float32 NCHW RGB tensor, scaled to 0-1 with no further
    /// normalization (matches D-FINE's own preprocessor exactly — see this class's own doc comment).
    /// No resize — VisionSession's ffmpeg filter chain produces the frame pre-sized. When
    /// GpuPreprocessing is on, this whole loop is replaced by <see cref="OnnxPreprocessHead"/>'s
    /// nodes running on the accelerator and <see cref="Detect"/> hands ORT the nv12 bytes directly.</summary>
    private DenseTensor<float> Preprocess(byte[] bgra)
    {
        var pixelCount = _profile.NetworkWidth * _profile.NetworkHeight;
        if (bgra.Length != pixelCount * 4)
        {
            throw new InvalidOperationException(
                $"Captured BGRA frame is {bgra.Length} bytes, expected {pixelCount * 4} for " +
                $"{_profile.NetworkWidth}x{_profile.NetworkHeight} — VisionSession's ffmpeg filter chain and this " +
                "engine's InferenceProfile have disagreed about the decode target.");
        }

        _detectTensor ??= new DenseTensor<float>([1, 3, _profile.NetworkHeight, _profile.NetworkWidth]);
        var tensorSpan = _detectTensor.Buffer.Span; // flat, row-major: [R plane][G plane][B plane], each H*W
        var gPlane = pixelCount;
        var bPlane = pixelCount * 2;

        for (var i = 0; i < pixelCount; i++)
        {
            var byteOffset = i * 4; // B,G,R,A
            tensorSpan[bPlane + i] = bgra[byteOffset] / 255f;
            tensorSpan[gPlane + i] = bgra[byteOffset + 1] / 255f;
            tensorSpan[i] = bgra[byteOffset + 2] / 255f;
        }

        return _detectTensor;
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}
