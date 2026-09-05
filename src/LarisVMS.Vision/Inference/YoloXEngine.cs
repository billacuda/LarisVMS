using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using YoloDotNet.Models;

namespace LarisVMS.Vision.Inference;

/// <summary>
/// YOLOX inference engine — runs directly on Microsoft.ML.OnnxRuntime via the same
/// <see cref="OrtSessionFactory"/> D-FINE uses (so CUDA / DirectML / OpenVINO / TensorRT / CPU all
/// carry over with no engine-specific code — see <see cref="DFineEngine"/>'s doc comment for why the
/// engine talks to ORT directly rather than through YoloDotNet's own wrapper).
///
/// <b>Preprocessing contract</b> (Megvii <c>demo/ONNXRuntime</c> canon — what <c>fetch_yolox.py</c>
/// pins, and what the OpenCV Model Zoo uses too): the captured frame is already at the network input
/// size (ffmpeg's own scale/pad chain — see <see cref="VisionSession"/>), so this only packs pixels:
/// BGRA8888 → <c>[1,3,H,W]</c> float32, channel order <b>BGR</b>, values kept in <b>0–255</b> (no
/// <c>/255</c>, no mean/std — YOLOX's default export normalizes nothing). Box decode (grid + stride)
/// and per-class NMS both happen in <see cref="YoloXDecoder"/>. If a pinned model turns out to want
/// RGB or a rescale, flip <see cref="ChannelOrderRgb"/> / add the rescale here and re-pin.
///
/// <b>Letterbox bars are black (0)</b>, not Megvii's 114 — verified empirically: with LarisVMS's
/// centre-pad geometry (Megvii corner-pads) a 114 fill made classification markedly worse (cars read
/// as trains/airplanes), where black is stable. Chasing Megvii parity would mean corner-pad + 114
/// together, not one without the other.
///
/// <b>GPU preprocessing</b> (<c>Detection.GpuPreprocessing</c>): not wired for YOLOX yet — the merged
/// nv12 head (<see cref="OnnxPreprocessHead"/>) is D-FINE-shaped (<c>/255</c> RGB). CameraDetectionPipeline
/// forces the frame to BGRA for a YOLOX pipeline, so that setting is a no-op here until a YOLOX head
/// variant lands.
///
/// <b>Batching</b> (<see cref="EngineOptions.BatchSize"/> &gt; 1, pass 3 of the detection/hardware-
/// acceleration overhaul): unlike D-FINE, the pinned Megvii export's <c>images</c> input is hardcoded
/// to batch 1 — <see cref="OnnxBatchAxis"/> rewrites the graph batch-dynamic in memory at load, only
/// when a batch is actually requested (a batch-1 pipeline never pays for or risks that rewrite; see
/// its own doc comment for what it does and how it was verified).
/// </summary>
public sealed class YoloXEngine : IDetectionEngine, IBatchDetectionEngine, ISlicedDetectionEngine
{
    /// <summary>Megvii's own <c>preproc</c> keeps cv2's BGR order (no <c>[:, :, ::-1]</c>). Set true
    /// only if a re-pinned export expects RGB.</summary>
    private const bool ChannelOrderRgb = false;

    /// <summary>The pinned export's own single input name — verified directly against the real
    /// downloaded <c>yolox_s.onnx</c> (2026-09-03: <c>images [1,3,640,640]</c>). Needed before the
    /// session exists (<see cref="_inputName"/> below is only discovered after), to build
    /// <see cref="EngineOptions.TensorRtBatchProfile"/> for a batched pipeline — see that property's
    /// own doc comment for why the caller has to supply it rather than <see cref="OrtSessionFactory"/>
    /// discovering it itself. Re-verify against a re-pinned export if this ever throws in
    /// <see cref="ValidateInputShape"/>'s own name check.</summary>
    private const string PinnedInputName = "images";

    private readonly InferenceSession _session;
    private readonly InferenceProfile _profile;
    private readonly SliceLayout? _sliceLayout;
    private readonly IReadOnlyList<string> _labels;
    private readonly ILogger<YoloXEngine> _logger;
    private readonly string _inputName;
    private readonly int _batchSize;
    private readonly DenseTensor<float> _inputTensor;
    private DenseTensor<float>? _batchTensor;
    private bool _loggedFirstInference;

    /// <param name="sliceLayout">Non-null only for <see cref="LarisVMS.Core.Enums.AspectMode.Slice"/>
    /// — see <see cref="ISlicedDetectionEngine"/>'s own doc comment and
    /// <see cref="DFineEngine"/>'s equivalent constructor parameter. Always implies GPU
    /// preprocessing (no CPU fallback for slicing); <paramref name="profile"/> is then the shared
    /// per-slice identity profile every slice decodes through, not a per-camera one.</param>
    public YoloXEngine(EngineOptions options, InferenceProfile profile, IReadOnlyList<string> labels, ILogger<YoloXEngine> logger,
        SliceLayout? sliceLayout = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(labels);
        _profile = profile;
        _sliceLayout = sliceLayout;
        _labels = labels;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _batchSize = options.BatchSize;

        if (sliceLayout is not null && !options.GpuPreprocessing)
        {
            throw new NotSupportedException(
                "AspectMode.Slice requires Detection.GpuPreprocessing — there is no CPU fallback for " +
                "frame slicing (see CameraDetectionPipeline's own doc comment).");
        }

        // Name this variant's own TensorRT engine cache entry before any session is built — every
        // branch below produces a differently shaped graph, and ONNX Runtime's own cache key cannot
        // tell them apart (see OnnxPreprocessHead.RetagGeneratedNames' doc comment). YOLOX only ever
        // merges a preprocessing head in Slice mode, so gpuPreprocessing is that same condition here.
        var opts = options with
        {
            TensorRtCacheKey = OrtSessionFactory.TensorRtCacheKeyFor(
                options.ModelPath, profile, sliceLayout, options.BatchSize, gpuPreprocessing: sliceLayout is not null),
        };

        var stopwatch = Stopwatch.StartNew();
        if (sliceLayout is not null)
        {
            // Composition order matters — see OnnxPreprocessHead.MergeSliced's own doc comment:
            // OnnxBatchAxis must rewrite the model's internal fixed-batch-1 Reshape targets *before*
            // the slicing head is merged in. numClasses = labels.Count, same as the plain-batch path.
            var batchDynamic = OnnxBatchAxis.MakeBatchDynamic(File.ReadAllBytes(opts.ModelPath), sliceLayout.Slices.Count, labels.Count);
            var sliced = OnnxPreprocessHead.MergeSliced(batchDynamic, sliceLayout, PinnedInputName,
                rescaleTo01: false, rgbChannelOrder: false);
            _session = new InferenceSession(sliced, OrtSessionFactory.Create(opts, logger));
        }
        else if (_batchSize > 1)
        {
            // OnnxBatchAxis.MakeBatchDynamic needs numClasses to recognize the export's own
            // fixed-batch-1 Reshape targets (numAttrs - 5) — labels.Count is exactly that.
            var rewritten = OnnxBatchAxis.MakeBatchDynamic(File.ReadAllBytes(opts.ModelPath), _batchSize, labels.Count);
            var sessionOptions = OrtSessionFactory.Create(
                opts with { TensorRtBatchProfile = (PinnedInputName, profile.NetworkHeight, profile.NetworkWidth) },
                logger);
            _session = new InferenceSession(rewritten, sessionOptions);
        }
        else
        {
            _session = new InferenceSession(opts.ModelPath, OrtSessionFactory.Create(opts, logger));
        }
        stopwatch.Stop();

        _inputName = _session.InputMetadata.Keys.First();
        if (sliceLayout is null && _batchSize > 1 && _inputName != PinnedInputName)
        {
            _logger.LogWarning(
                "YOLOX model's own input is named '{Actual}', not the expected '{Expected}' — the " +
                "TensorRT batch profile pinned in EngineOptions.TensorRtBatchProfile names the wrong " +
                "input and may be ignored or rejected. Re-verify PinnedInputName against this export.",
                _inputName, PinnedInputName);
        }
        // Sliced mode's real input is "nv12" (uint8, rank 3) — a completely different shape contract
        // ValidateInputShape has no business checking; DetectSliced validates its own byte length.
        if (sliceLayout is null) ValidateInputShape();
        _inputTensor = new DenseTensor<float>([1, 3, profile.NetworkHeight, profile.NetworkWidth]);

        _logger.LogInformation(
            "Loaded YOLOX model ({Classes} classes, input '{Input}') from {Path} in {LoadMs} ms (batch size: {BatchSize}, sliced: {Sliced}).",
            _labels.Count, _inputName, options.ModelPath, stopwatch.ElapsedMilliseconds, _batchSize, sliceLayout is not null);
    }

    public double? LastInferenceMilliseconds { get; private set; }

    public List<ObjectDetection> Detect(byte[] frame, double confidence = 0.25, double iou = 0.5)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var stopwatch = Stopwatch.StartNew();

        var input = NamedOnnxValue.CreateFromTensor(_inputName, Preprocess(frame, _inputTensor, imageIndex: 0));
        using var outputs = _session.Run([input]);

        var outTensor = outputs.First().AsTensor<float>();
        var dims = outTensor.Dimensions;
        if (dims.Length != 3 || dims[0] != 1 || dims[2] < 6)
            throw new InvalidOperationException(
                $"YOLOX ONNX output shape [{string.Join(",", dims.ToArray())}] is not [1, N, 5+numClasses] — " +
                "this build expects the single concatenated output (Megvii's own export shape). A split " +
                "three-output export is not supported. Re-pin the model with tools/export-models/fetch_yolox.py.");

        var numAnchors = dims[1];
        var numAttrs = dims[2];

        // Read the output straight out of the tensor's own buffer. ToArray() here copied
        // numAnchors*numAttrs floats per frame — 8400x85 is 2.86 MB, a Large Object Heap allocation
        // on every single inference, six times over on a six-camera node. The decoder only ever reads
        // forward through the span, and `outputs` is still alive for the whole call, so there is
        // nothing to own. The ToArray fallback stays for a tensor that isn't dense-backed (ORT
        // returns DenseTensor today; this is not contractual).
        var results = outTensor is DenseTensor<float> dense
            ? YoloXDecoder.Decode(dense.Buffer.Span, numAnchors, numAttrs, _labels, confidence, iou, _profile)
            : YoloXDecoder.Decode(outTensor.ToArray(), numAnchors, numAttrs, _labels, confidence, iou, _profile);

        stopwatch.Stop();
        LastInferenceMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        if (!_loggedFirstInference)
        {
            _loggedFirstInference = true;
            // Same reasoning as DFineEngine's first-inference log: no ORT API reports which execution
            // provider actually bound, so a timing sanity-check is the only confirmation a GPU
            // provider loaded rather than silently falling back to CPU.
            _logger.LogInformation(
                "First YOLOX inference: {Ms:F1} ms ({Anchors} anchors, {Attrs} attrs; includes provider warmup).",
                LastInferenceMilliseconds, numAnchors, numAttrs);
        }

        return results;
    }

    /// <summary>Currently unused (the planned frame-slicing overhaul is the first real caller) — one
    /// genuine batched <see cref="_session"/> forward pass over every image at once via
    /// <see cref="OnnxBatchAxis"/>'s graph rewrite, not a loop. <paramref name="images"/>.Count must
    /// equal this engine's own fixed <see cref="EngineOptions.BatchSize"/> — see
    /// <see cref="DFineEngine.DetectBatch"/>'s own doc comment for why a real batched Run call can't
    /// vary its shape call to call.</summary>
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
        var n = images.Count; // == _batchSize, checked above
        _batchTensor ??= new DenseTensor<float>([_batchSize, 3, _profile.NetworkHeight, _profile.NetworkWidth]);

        // IBatchDetectionEngine's own contract is nv12 bytes (see its doc comment) — this engine has
        // no GPU preprocessing head of its own yet (see this class's own doc comment), so every image
        // is converted to BGR pixels on the CPU here rather than fed to ORT as nv12.
        for (var i = 0; i < n; i++)
        {
            var bgra = Nv12ToBgra(images[i].Nv12, _profile.NetworkWidth, _profile.NetworkHeight);
            Preprocess(bgra, _batchTensor, i);
        }

        var input = NamedOnnxValue.CreateFromTensor(_inputName, _batchTensor);
        using var outputs = _session.Run([input]);

        var outTensor = outputs.First().AsTensor<float>();
        var dims = outTensor.Dimensions;
        if (dims.Length != 3 || dims[0] != n || dims[2] < 6)
            throw new InvalidOperationException(
                $"YOLOX batched ONNX output shape [{string.Join(",", dims.ToArray())}] is not [{n}, N, 5+numClasses].");

        var numAnchors = dims[1];
        var numAttrs = dims[2];
        // Explicitly typed (not var) so both branches convert to the same ReadOnlySpan<float> rather
        // than leaving the compiler to find a common type between Span<float> and float[] — the same
        // reasoning Detect's own two-branch call above relies on, just needing a named variable here
        // since it's sliced per image below instead of passed straight to one Decode call.
        ReadOnlySpan<float> flat = outTensor is DenseTensor<float> dense ? dense.Buffer.Span : outTensor.ToArray();
        var perImage = numAnchors * numAttrs;

        var results = new List<List<ObjectDetection>>(n);
        for (var i = 0; i < n; i++)
        {
            var imageOutput = flat.Slice(i * perImage, perImage);
            // iou fixed at 0.5 (Megvii's own default) — DetectBatch has no per-call iou parameter of
            // its own (IBatchDetectionEngine's contract, matching DFineEngine's DetectBatch, which
            // has no iou at all since D-FINE never needs per-class NMS in the first place).
            results.Add(YoloXDecoder.Decode(imageOutput, numAnchors, numAttrs, _labels, confidence, 0.5, images[i].Profile));
        }

        stopwatch.Stop();
        LastInferenceMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        return results;
    }

    /// <summary>GPU-native Slice-mode entry point — see <see cref="ISlicedDetectionEngine"/>'s own
    /// doc comment and <see cref="DFineEngine.DetectSliced"/>'s equivalent. One nv12 buffer, the
    /// whole <see cref="_sliceLayout"/> capture frame, in; one forward pass; the merged head's own
    /// Slice+Concat already produced the <c>[N,…]</c> batch, so this only decodes each of its N
    /// slots.</summary>
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
        using var outputs = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, nv12Tensor)]);

        var outTensor = outputs.First().AsTensor<float>();
        var dims = outTensor.Dimensions;
        var n = dims[0];
        if (dims.Length != 3 || n != layout.Slices.Count || dims[2] < 6)
            throw new InvalidOperationException(
                $"YOLOX sliced ONNX output shape [{string.Join(",", dims.ToArray())}] is not [{layout.Slices.Count}, N, 5+numClasses].");

        var numAnchors = dims[1];
        var numAttrs = dims[2];
        ReadOnlySpan<float> flat = outTensor is DenseTensor<float> dense ? dense.Buffer.Span : outTensor.ToArray();
        var perImage = numAnchors * numAttrs;

        var results = new List<List<ObjectDetection>>(n);
        for (var i = 0; i < n; i++)
        {
            var imageOutput = flat.Slice(i * perImage, perImage);
            // _profile is the shared per-slice identity profile (see this class's own constructor
            // doc comment) — every slice decodes through the identical transform.
            results.Add(YoloXDecoder.Decode(imageOutput, numAnchors, numAttrs, _labels, confidence, 0.5, _profile));
        }

        stopwatch.Stop();
        LastInferenceMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        return results;
    }

    /// <summary>Plain nv12 → BGRA8888 conversion (BT.601 limited range, matching every other nv12
    /// consumer in this codebase) so <see cref="Preprocess"/>'s existing BGRA-packing logic can stay
    /// exactly as-is for the batched path too, rather than duplicating it for nv12. Not the hot path
    /// this engine cares about optimizing — DetectBatch runs on a trigger, not every frame.</summary>
    private static byte[] Nv12ToBgra(byte[] nv12, int w, int h)
    {
        var bgra = new byte[w * h * 4];
        var srcUv = w * h;
        for (var y = 0; y < h; y++)
        {
            var cyRow = srcUv + (y / 2) * w;
            for (var x = 0; x < w; x++)
            {
                var cxByte = (x / 2) * 2;
                var (r, g, b) = Bt601Limited.ToRgb(nv12[y * w + x], nv12[cyRow + cxByte], nv12[cyRow + cxByte + 1]);
                var o = (y * w + x) * 4;
                bgra[o] = b; bgra[o + 1] = g; bgra[o + 2] = r; bgra[o + 3] = 255;
            }
        }
        return bgra;
    }

    /// <summary>Packs one BGRA8888 frame into image <paramref name="imageIndex"/>'s own
    /// <c>3*H*W</c>-float slice of <paramref name="tensor"/> — <c>[1,3,H,W]</c> for
    /// <see cref="Detect"/>'s single-image tensor (always index 0), <c>[N,3,H,W]</c> for
    /// <see cref="DetectBatch"/>'s shared batch tensor.</summary>
    private DenseTensor<float> Preprocess(byte[] bgra, DenseTensor<float> tensor, int imageIndex)
    {
        var pixelCount = _profile.NetworkWidth * _profile.NetworkHeight;
        if (bgra.Length != pixelCount * 4)
            throw new InvalidOperationException(
                $"Captured BGRA frame is {bgra.Length} bytes, expected {pixelCount * 4} for " +
                $"{_profile.NetworkWidth}x{_profile.NetworkHeight} — VisionSession's ffmpeg filter chain and this " +
                "engine's InferenceProfile have disagreed about the decode target.");

        // Reused across calls rather than allocated per frame — see this class's own field doc
        // comments for why Detect and DetectBatch each keep their own tensor. ONNX Runtime has copied
        // the tensor to the device by the time Run returns, and every element written here is
        // rewritten below, so there is no stale data to clear first.
        var t = tensor.Buffer.Span; // planar: [plane0][plane1][plane2] per image, each H*W
        var imageBase = imageIndex * pixelCount * 3;
        var plane1 = imageBase + pixelCount;
        var plane2 = imageBase + pixelCount * 2;

        // BGRA byte order is B,G,R,A. Planar output is RGB when ChannelOrderRgb, else BGR — no /255.
        var rPlane = ChannelOrderRgb ? imageBase : plane2;
        var bPlane = ChannelOrderRgb ? plane2 : imageBase;

        for (var i = 0; i < pixelCount; i++)
        {
            var o = i * 4;
            t[bPlane + i] = bgra[o];         // B
            t[plane1 + i] = bgra[o + 1];     // G
            t[rPlane + i] = bgra[o + 2];     // R
        }

        return tensor;
    }

    private void ValidateInputShape()
    {
        var dims = _session.InputMetadata[_inputName].Dimensions; // expected [1, 3, H, W]
        if (dims.Length != 4)
        {
            _logger.LogWarning("YOLOX input '{Input}' has rank {Rank}, expected 4 ([1,3,H,W]) — proceeding anyway.", _inputName, dims.Length);
            return;
        }

        var h = dims[2];
        var w = dims[3];
        // -1 = dynamic axis; only a fixed dim that disagrees with the profile is a real problem.
        if (h > 0 && w > 0 && (h != _profile.NetworkHeight || w != _profile.NetworkWidth))
            throw new InvalidOperationException(
                $"YOLOX model expects a fixed {w}x{h} input but the pipeline is decoding frames at " +
                $"{_profile.NetworkWidth}x{_profile.NetworkHeight}. The YoloXSize → network-size mapping " +
                "(DetectionModelCatalog.GetYoloXNetworkSize) must match the pinned export.");
    }

    public void Dispose() => _session.Dispose();
}
