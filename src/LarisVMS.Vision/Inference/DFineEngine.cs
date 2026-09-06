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
public sealed class DFineEngine : IDetectionEngine, IBatchDetectionEngine, ISlicedDetectionEngine
{
    // The one session for this camera. When GpuPreprocessing is on it has the nv12 -> normalized
    // tensor head merged in (OnnxPreprocessHead) and its input is "nv12"; otherwise its input is the
    // model's own "pixel_values" and the CPU packs it. Both Detect and DetectBatch feed 640x640 nv12
    // through this same session — D-FINE's own export was already batch-dynamic before this project
    // ever touched it, so DetectBatch's single-Run batch (pass 3) needed no graph change here, unlike
    // YoloXEngine's OnnxBatchAxis rewrite.
    private readonly InferenceSession _session;

    private readonly bool _gpuPreprocessing;
    private readonly InferenceProfile _profile;
    private readonly SliceLayout? _sliceLayout;
    private readonly IReadOnlyList<string> _labels;
    private readonly ILogger<DFineEngine> _logger;
    private readonly int _batchSize;
    private bool _loggedFirstInference;

    // Throttle for the non-finite-output warning below — checked only on the zero-detection path,
    // logged at most once per interval so a persistently broken engine doesn't flood the log.
    private DateTime _lastNonFiniteWarnUtc = DateTime.MinValue;
    private static readonly TimeSpan NonFiniteWarnInterval = TimeSpan.FromSeconds(30);

    // Input tensors reused across calls rather than allocated per frame — at 640x640 each is 4.92 MB
    // (H*W*3 floats) per image, a Large Object Heap allocation every inference, which on a busy node
    // forces several gen2 collections per second. ONNX Runtime has copied the tensor to the device by
    // the time Run returns, so the next call is free to overwrite it.
    //
    // Two of them, not one, because this engine has two entry points that could run concurrently on
    // separate tasks (Detect from the continuous per-camera inference loop, DetectBatch from a
    // batched caller). Each preprocessor has exactly one of those callers (Preprocess <- Detect,
    // PreprocessNv12Into <- DetectBatch), so a buffer per preprocessor keeps each one single-threaded.
    // Sharing a single tensor between them would be a race. Allocated lazily so the path a given
    // deployment doesn't use costs nothing. _batchTensor's first dim is _batchSize, not 1 — see
    // PreprocessNv12Into's own doc comment.
    private DenseTensor<float>? _detectTensor;
    private DenseTensor<float>? _batchTensor;
    private byte[]? _batchNv12Buffer;

    /// <param name="sliceLayout">Non-null only for <see cref="LarisVMS.Core.Enums.AspectMode.Slice"/>
    /// — see <see cref="ISlicedDetectionEngine"/>'s own doc comment. Requires
    /// <paramref name="options"/>.GpuPreprocessing (Slice mode has no CPU fallback — see
    /// CameraDetectionPipeline's own doc comment for why). <paramref name="profile"/> is then the
    /// shared per-slice *identity* profile (network-size square, Stretch) every slice decodes
    /// through — not a per-camera profile, since a Slice camera has no single square one (its own
    /// capture buffer is <paramref name="sliceLayout"/>'s own non-square CaptureWidth×Height).</param>
    public DFineEngine(EngineOptions options, InferenceProfile profile, IReadOnlyList<string> labels, ILogger<DFineEngine> logger,
        SliceLayout? sliceLayout = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(labels);
        _profile = profile;
        _sliceLayout = sliceLayout;
        _labels = labels;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _gpuPreprocessing = options.GpuPreprocessing;
        _batchSize = options.BatchSize;

        if (sliceLayout is not null && !_gpuPreprocessing)
        {
            throw new NotSupportedException(
                "AspectMode.Slice requires Detection.GpuPreprocessing — there is no CPU fallback for " +
                "frame slicing (see CameraDetectionPipeline's own doc comment).");
        }

        // The caller (CameraDetectionPipeline) knows only that batching is wanted, not this graph's
        // own input name — see EngineOptions.TensorRtBatchProfile's own doc comment for why that has
        // to be filled in here rather than by OrtSessionFactory itself. Not applied for Slice mode:
        // its own ORT-level input ("nv12", built below) is always batch 1 — one physical frame — the
        // N-way batching is an internal graph detail TensorRT never sees as an external dimension.
        //
        // TensorRtCacheKey goes on unconditionally alongside it: each of the three branches below
        // builds a differently shaped graph, and ONNX Runtime's own engine cache key cannot tell them
        // apart (see OnnxPreprocessHead.RetagGeneratedNames' doc comment for the collision that
        // caused).
        var opts = options with
        {
            TensorRtCacheKey = OrtSessionFactory.TensorRtCacheKeyFor(
                options.ModelPath, profile, sliceLayout, options.BatchSize, _gpuPreprocessing),
        };

        var sessionOptions = _batchSize > 1 && sliceLayout is null
            ? OrtSessionFactory.Create(opts with
              {
                  TensorRtBatchProfile = (_gpuPreprocessing ? "nv12" : "pixel_values", profile.NetworkHeight, profile.NetworkWidth),
              }, logger)
            : OrtSessionFactory.Create(opts, logger);

        var stopwatch = Stopwatch.StartNew();
        if (sliceLayout is not null)
        {
            var merged = OnnxPreprocessHead.MergeSliced(File.ReadAllBytes(opts.ModelPath), sliceLayout,
                "pixel_values", rescaleTo01: true, rgbChannelOrder: true);
            _session = new InferenceSession(merged, sessionOptions);
        }
        else if (_gpuPreprocessing)
        {
            var merged = OnnxPreprocessHead.Merge(File.ReadAllBytes(opts.ModelPath),
                profile.NetworkWidth, profile.NetworkHeight);
            _session = new InferenceSession(merged, sessionOptions);
        }
        else
        {
            _session = new InferenceSession(opts.ModelPath, sessionOptions);
        }
        stopwatch.Stop();

        _logger.LogInformation(
            "Loaded D-FINE model ({Classes} classes) from {Path} in {LoadMs} ms (GPU preprocessing: {Gpu}, batch size: {BatchSize}, sliced: {Sliced}).",
            _labels.Count, options.ModelPath, stopwatch.ElapsedMilliseconds, _gpuPreprocessing, _batchSize, sliceLayout is not null);
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

        var logits = logitsTensor.ToArray();
        var boxes = boxesTensor.ToArray();
        var results = DFineDecoder.Decode(logits, boxes, numQueries, numClasses, _labels, confidence, _profile);
        if (results.Count == 0) WarnIfNonFinite(logits, boxes);

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

    /// <summary>Currently unused (the planned frame-slicing overhaul is the first real caller) — one
    /// genuine batched <see cref="_session"/> forward pass over every image at once, not a loop:
    /// D-FINE's own export already has a dynamic batch axis (verified directly against the real
    /// model — see <see cref="OnnxBatchAxis"/>'s own doc comment for the equivalent YOLOX finding,
    /// which needed graph surgery where D-FINE didn't), so batching it needed only a batch-shaped
    /// input tensor and a per-image slice of the batched output, no graph change at all.
    /// <paramref name="images"/>.Count must equal this
    /// engine's own fixed <see cref="EngineOptions.BatchSize"/> (constant for this pipeline's whole
    /// lifetime) — a real single ONNX Runtime Run call can't vary its batch dimension call to call
    /// without either a resize (defeats the point of pinning TensorRT's profile to one shape) or
    /// padding with dummy images (wasted inference work), and this pipeline's own caller already
    /// knows exactly how many images it always has (its own slice count). Each image still carries
    /// its own <see cref="InferenceProfile"/> for <see cref="DFineDecoder.Decode"/> — a real
    /// letterbox profile for a whole frame, an identity Stretch profile for a plain crop.</summary>
    public List<List<ObjectDetection>> DetectBatch(IReadOnlyList<(byte[] Nv12, InferenceProfile Profile)> images, double confidence)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 0) return [];
        if (images.Count != _batchSize)
            throw new InvalidOperationException(
                $"DetectBatch was called with {images.Count} image(s) but this engine was built for a fixed " +
                $"batch size of {_batchSize} (EngineOptions.BatchSize) — the caller's own slice count must " +
                "never vary between calls.");

        var stopwatch = Stopwatch.StartNew();
        var n = images.Count;
        var expectedNv12 = _profile.NetworkWidth * _profile.NetworkHeight * 3 / 2;

        List<NamedOnnxValue> inputs;
        if (_gpuPreprocessing)
        {
            // One contiguous [n, H*3/2, W] byte buffer — every image's nv12 bytes copied in, back to
            // back. Reused across calls like every other buffer here; sized once at n (== _batchSize,
            // fixed for this pipeline's lifetime).
            _batchNv12Buffer ??= new byte[n * expectedNv12];
            for (var i = 0; i < n; i++)
            {
                var (nv12, _) = images[i];
                if (nv12.Length != expectedNv12)
                    throw new InvalidOperationException(
                        $"DetectBatch image {i} is {nv12.Length} bytes, expected {expectedNv12} ({_profile.NetworkWidth}x{_profile.NetworkHeight} nv12).");
                Array.Copy(nv12, 0, _batchNv12Buffer, i * expectedNv12, expectedNv12);
            }
            inputs = [NamedOnnxValue.CreateFromTensor("nv12",
                new DenseTensor<byte>(_batchNv12Buffer, [n, _profile.NetworkHeight * 3 / 2, _profile.NetworkWidth]))];
        }
        else
        {
            for (var i = 0; i < n; i++) PreprocessNv12Into(images[i].Nv12, i);
            inputs = [NamedOnnxValue.CreateFromTensor("pixel_values", _batchTensor!)];
        }

        using var outputs = _session.Run(inputs);
        var logitsTensor = outputs.First(o => o.Name == "logits").AsTensor<float>();
        var boxesTensor = outputs.First(o => o.Name == "pred_boxes").AsTensor<float>();
        var numQueries = logitsTensor.Dimensions[1];
        var numClasses = logitsTensor.Dimensions[2];
        var logits = logitsTensor.ToArray(); // [n, numQueries, numClasses], row-major
        var boxes = boxesTensor.ToArray(); // [n, numQueries, 4], row-major

        var logitsPerImage = numQueries * numClasses;
        var boxesPerImage = numQueries * 4;
        var results = new List<List<ObjectDetection>>(n);
        for (var i = 0; i < n; i++)
        {
            // A plain span slice, not a copy — Decode only ever reads forward through it, and the
            // backing arrays (from ToArray() above) are alive for this whole loop regardless.
            var imageLogits = logits.AsSpan(i * logitsPerImage, logitsPerImage);
            var imageBoxes = boxes.AsSpan(i * boxesPerImage, boxesPerImage);
            var imageResults = DFineDecoder.Decode(imageLogits, imageBoxes, numQueries, numClasses, _labels, confidence, images[i].Profile);
            if (imageResults.Count == 0) WarnIfNonFinite(imageLogits, imageBoxes);
            results.Add(imageResults);
        }

        stopwatch.Stop();
        LastInferenceMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        return results;
    }

    /// <summary>GPU-native Slice-mode entry point — see <see cref="ISlicedDetectionEngine"/>'s own
    /// doc comment. One nv12 buffer, the whole <see cref="_sliceLayout"/> capture frame, in; one
    /// forward pass; the merged head's own Slice+Concat already produced the <c>[N,…]</c> batch, so
    /// this only has to decode each of its N slots — the same per-image decode loop
    /// <see cref="DetectBatch"/> uses, just fed by the graph's own internal split instead of N
    /// separately-packed input images.</summary>
    public List<List<ObjectDetection>> DetectSliced(byte[] wholeFrameNv12, double confidence)
    {
        ArgumentNullException.ThrowIfNull(wholeFrameNv12);
        if (_sliceLayout is not { } layout)
            throw new InvalidOperationException("DetectSliced requires this engine to have been built with a SliceLayout.");

        var expected = layout.CaptureWidth * layout.CaptureHeight * 3 / 2;
        if (wholeFrameNv12.Length != expected)
            throw new InvalidOperationException(
                $"Whole-frame nv12 is {wholeFrameNv12.Length} bytes, expected {expected} for {layout.CaptureWidth}x{layout.CaptureHeight}.");

        var stopwatch = Stopwatch.StartNew();
        var nv12Tensor = new DenseTensor<byte>(wholeFrameNv12, [1, layout.CaptureHeight * 3 / 2, layout.CaptureWidth]);
        List<NamedOnnxValue> inputs = [NamedOnnxValue.CreateFromTensor("nv12", nv12Tensor)];

        using var outputs = _session.Run(inputs);
        var logitsTensor = outputs.First(o => o.Name == "logits").AsTensor<float>();
        var boxesTensor = outputs.First(o => o.Name == "pred_boxes").AsTensor<float>();
        var n = logitsTensor.Dimensions[0];
        var numQueries = logitsTensor.Dimensions[1];
        var numClasses = logitsTensor.Dimensions[2];
        var logits = logitsTensor.ToArray();
        var boxes = boxesTensor.ToArray();

        var logitsPerImage = numQueries * numClasses;
        var boxesPerImage = numQueries * 4;
        var results = new List<List<ObjectDetection>>(n);
        for (var i = 0; i < n; i++)
        {
            var imageLogits = logits.AsSpan(i * logitsPerImage, logitsPerImage);
            var imageBoxes = boxes.AsSpan(i * boxesPerImage, boxesPerImage);
            // _profile here is the shared per-slice identity profile (see this class's own
            // constructor doc comment) — every slice decodes through the identical transform.
            var imageResults = DFineDecoder.Decode(imageLogits, imageBoxes, numQueries, numClasses, _labels, confidence, _profile);
            if (imageResults.Count == 0) WarnIfNonFinite(imageLogits, imageBoxes);
            results.Add(imageResults);
        }

        stopwatch.Stop();
        LastInferenceMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        return results;
    }

    /// <summary>
    /// Called only when a frame decoded to zero detections: if the raw model output holds any NaN/Inf
    /// value, every candidate was silently dropped by <see cref="DFineDecoder"/> (a NaN fails every
    /// comparison, then clamps to a degenerate box). That is the signature of the TensorRT FP16
    /// builder overflowing on D-FINE's transformer activations — otherwise invisible, since no
    /// exception is thrown. Throttled so a persistently broken engine logs once per interval, not
    /// once per frame.
    /// </summary>
    private void WarnIfNonFinite(ReadOnlySpan<float> logits, ReadOnlySpan<float> boxes)
    {
        if (!HasNonFinite(logits) && !HasNonFinite(boxes)) return;

        var now = DateTime.UtcNow;
        if (now - _lastNonFiniteWarnUtc < NonFiniteWarnInterval) return;
        _lastNonFiniteWarnUtc = now;

        _logger.LogWarning(
            "D-FINE inference produced non-finite (NaN/Inf) logits or boxes — every detection was dropped. " +
            "This is the TensorRT FP16-overflow signature for D-FINE. Set Detection.DFineTensorRtMode to FP32 " +
            "(or Off) for this node in Admin > Settings > Detection, or its per-node override on Admin > Nodes.");
    }

    private static bool HasNonFinite(ReadOnlySpan<float> values)
    {
        foreach (var v in values)
            if (!float.IsFinite(v)) return true;
        return false;
    }

    /// <summary>CPU fallback (GpuPreprocessing off) for <see cref="DetectBatch"/>: packs one
    /// 640x640 packed-nv12 frame into image <paramref name="imageIndex"/>'s own slice of the shared
    /// <c>[_batchSize,3,H,W]</c> float32 RGB tensor (/255, BT.601 limited range), allocated lazily on
    /// the first call at the fixed batch size this engine was built for. Each image occupies its own
    /// contiguous <c>3*H*W</c> region — R plane, G plane, B plane, same per-image layout <see
    /// cref="Preprocess"/> uses for the single-image tensor, just repeated <c>_batchSize</c> times
    /// back to back.</summary>
    private void PreprocessNv12Into(byte[] nv12, int imageIndex)
    {
        int w = _profile.NetworkWidth, h = _profile.NetworkHeight;
        var pixelCount = w * h;
        _batchTensor ??= new DenseTensor<float>([_batchSize, 3, h, w]);
        var t = _batchTensor.Buffer.Span;
        var imageBase = imageIndex * pixelCount * 3;
        var gPlane = imageBase + pixelCount;
        var bPlane = imageBase + pixelCount * 2;

        for (var y = 0; y < h; y++)
        {
            var cyRow = pixelCount + (y / 2) * w;
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                var cxByte = (x / 2) * 2;
                var (r, g, b) = Bt601Limited.ToRgb(nv12[i], nv12[cyRow + cxByte], nv12[cyRow + cxByte + 1]);
                t[imageBase + i] = r / 255f;
                t[gPlane + i] = g / 255f;
                t[bPlane + i] = b / 255f;
            }
        }
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
