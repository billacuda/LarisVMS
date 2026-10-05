using System.Diagnostics;
using LarisVMS.Core.Enums;
using LarisVMS.Vision.Inference.Decoders;
using LarisVMS.Vision.Models;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using YoloDotNet.Models;

namespace LarisVMS.Vision.Inference;

/// <summary>
/// Detection engine for a model discovered by <see cref="ModelDiscovery"/> — the descriptor-driven
/// counterpart to <see cref="DFineEngine"/>/<see cref="YoloXEngine"/> for any model whose decode
/// behavior comes from its own ONNX metadata or a JSON sidecar rather than a hand-written engine
/// class. Runs the same <see cref="OrtSessionFactory"/>/<see cref="InferenceSession"/> plumbing as the
/// built-in engines, and decodes through whichever <see cref="IDetectionDecoder"/>
/// <see cref="Decoders.DecoderFactory"/> resolved for it.
///
/// <b><see cref="AspectMode.Slice"/> defaults to CPU-cropped, not GPU-native like DFineEngine/
/// YoloXEngine — with one opportunistic, numerically-verified exception.</b> Their own Slice mode
/// merges a preprocessing head that cuts one whole frame into N tiles *inside* the ONNX graph as a
/// batch dimension (<see cref="OnnxPreprocessHead.MergeSliced"/>), which only works because their two
/// specific export shapes are already known to tolerate (D-FINE) or have been hand-verified and
/// rewritten for (YOLOX, <see cref="OnnxBatchAxis"/>) a batch size greater than 1. A plain Ultralytics
/// ONNX export (<c>model.export(format="onnx")</c>) hardcodes batch=1 internally instead.
///
/// <b>"ONNX Runtime will throw if the graph can't batch" is false — an earlier version of this class's
/// own doc comment claimed exactly that, and shipping on that assumption caused a real production
/// regression.</b> ONNX's own `Reshape` with a `-1` dimension silently *absorbs* an unexpected batch
/// size instead of erroring — <see cref="OnnxBatchAxis"/>'s own doc comment documents this precisely
/// as the reason YOLOX needed hand-verified graph surgery rather than trusting shape inference alone.
/// Concretely: Ultralytics' end-to-end NMS head (YOLOv10/YOLO26, exported with `dynamic=True`) builds a
/// per-batch gather index from `torch.arange(batch_size)`, which gets constant-folded to a literal at
/// export *trace* time — `dynamic=True` only marks shape metadata as symbolic, it does not un-freeze
/// already-traced values. The resulting merged graph loads without error, produces the *correct shape*,
/// and is *silently wrong*: every tile's detections come out carrying tile 0's box coordinates. Shape
/// inference cannot catch this; only running the graph and checking its actual numbers can.
///
/// So the opportunistic path here does exactly that. When the model's own declared input batch
/// dimension isn't hardcoded to 1, this class builds the merged graph and then, before trusting it,
/// runs one batched pass and N separate single-tile passes on the same synthetic frame and compares
/// every output tensor's per-slot values (see <see cref="TryBuildGpuNativeSlicedSession"/> and
/// <see cref="VerifyBatchedOutputsMatchSingleImage"/>). Only a session whose batched output actually
/// agrees, tile by tile, with the single-image ground truth is accepted; any mismatch, ONNX Runtime
/// exception, or unsupported normalize convention falls back to the CPU-per-tile path, which is always
/// correct because it never asks the model's own graph to batch at all.
///
/// Either way, the CPU-per-tile path reads each tile straight out of the whole captured nv12 frame on
/// the CPU (<see cref="PreprocessNv12Tile"/>, into the same reused input tensor <see cref="Detect"/>
/// fills) and runs N separate batch-1 forward passes.
///
/// Batching (<see cref="IBatchDetectionEngine"/>) is not implemented.
/// </summary>
public sealed class GenericOnnxEngine : IDetectionEngine, ISlicedDetectionEngine
{
    private readonly SessionLease _lease;
    private readonly InferenceProfile _profile;
    private readonly ModelDescriptor _descriptor;
    private readonly IDetectionDecoder _decoder;
    private readonly IReadOnlyList<string> _labels;
    private readonly ILogger _logger;
    private readonly string _inputName;
    private readonly bool _channelOrderRgb;
    private readonly SliceLayout? _sliceLayout;
    private readonly bool _gpuNativeSlicing;
    private readonly DenseTensor<float> _inputTensor;
    private bool _loggedFirstInference;

    /// <summary>Whether construction accepted the GPU-native batched Slice path (after numeric
    /// verification, see <see cref="TryBuildGpuNativeSlicedSession"/>) rather than the CPU-per-tile
    /// fallback. Internal — lets this decision's own tests observe the outcome directly rather than
    /// inferring it from log output.</summary>
    internal bool IsGpuNativeSlicing => _gpuNativeSlicing;

    /// <param name="sliceLayout">Non-null only for <see cref="AspectMode.Slice"/> — see this class's
    /// own doc comment for the GPU-native-attempt-then-CPU-fallback design. <paramref name="profile"/>
    /// is then the shared per-slice identity profile every slice decodes through (the caller's
    /// responsibility, same convention the built-in engines already rely on) — not a per-camera
    /// profile, since a Slice camera has no single square one.</param>
    public GenericOnnxEngine(EngineOptions options, ModelDescriptor descriptor, IDetectionDecoder decoder,
        IReadOnlyList<string> labels, InferenceProfile profile, ILogger<GenericOnnxEngine> logger,
        SliceLayout? sliceLayout = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(profile);
        _descriptor = descriptor;
        _decoder = decoder;
        _labels = labels;
        _profile = profile;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _channelOrderRgb = descriptor.ChannelOrder.Equals("RGB", StringComparison.OrdinalIgnoreCase);
        _sliceLayout = sliceLayout;

        // Always built first: needed to discover the model's own input name and (for Slice mode)
        // whether its declared batch dimension looks dynamic, and it's the CPU-per-tile fallback's
        // own session if the GPU-native attempt below either isn't tried or doesn't pan out.
        var plainOpts = options with
        {
            TensorRtCacheKey = OrtSessionFactory.TensorRtCacheKeyFor(options.ModelPath, profile, sliceLayout: null,
                batchSize: 1, gpuPreprocessing: false),
        };

        var stopwatch = Stopwatch.StartNew();
        var plainLease = SharedSessionPool.Acquire(SharedSessionPool.KeyFor(plainOpts), options.MaxCamerasPerSession,
            () => new InferenceSession(options.ModelPath, OrtSessionFactory.Create(plainOpts, logger)));
        _inputName = plainLease.Session.InputMetadata.Keys.First();
        // Allocated before the GPU-native attempt below: Preprocess (used by both the CPU-per-tile
        // fallback and the verification step's own single-tile runs) needs it either way.
        _inputTensor = new DenseTensor<float>([1, 3, profile.NetworkHeight, profile.NetworkWidth]);

        SessionLease? gpuNativeLease = null;
        if (sliceLayout is not null && DeclaresPossiblyDynamicBatch(plainLease.Session, _inputName))
        {
            gpuNativeLease = TryBuildGpuNativeSlicedSession(options, sliceLayout, profile, plainLease.Session, logger);
        }

        if (gpuNativeLease is not null)
        {
            plainLease.Dispose();
            _lease = gpuNativeLease;
            _gpuNativeSlicing = true;
        }
        else
        {
            _lease = plainLease;
            _gpuNativeSlicing = false;
        }
        stopwatch.Stop();

        if (!_gpuNativeSlicing) ValidateInputShape();

        _logger.LogInformation(
            "Loaded descriptor-driven model ({Classes} classes, decoder {Decoder}, input '{Input}') from {Path} in {LoadMs} ms " +
            "(sliced: {Sliced}, slicing mode: {SliceMode}, session: {Session}).",
            _labels.Count, descriptor.Decoder, _inputName, options.ModelPath, stopwatch.ElapsedMilliseconds,
            sliceLayout is not null, sliceLayout is null ? "n/a" : _gpuNativeSlicing ? "GPU-native" : "CPU-per-tile",
            _lease.IsShared ? $"shared, camera {_lease.LeaseNumber} of up to {options.MaxCamerasPerSession}" : "new");
    }

    public double? LastInferenceMilliseconds { get; private set; }

    public List<ObjectDetection> Detect(byte[] frame, double confidence = 0.25, double iou = 0.5)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_gpuNativeSlicing)
            throw new InvalidOperationException(
                "Detect() cannot be called on a GPU-native-sliced engine — its session's only input is " +
                "the merged 'nv12' whole-frame tensor, not this model's own single-image input. Callers " +
                "must use DetectSliced instead; CameraDetectionPipeline never calls Detect() on a " +
                "Slice-mode camera, so reaching this means something upstream is misusing the engine.");

        var stopwatch = Stopwatch.StartNew();

        var input = NamedOnnxValue.CreateFromTensor(_inputName, Preprocess(frame));
        using var outputs = _lease.Run([input]);

        var thresholds = new DecodeThresholds((float)confidence, (float)iou, _descriptor.MaxDetections, _descriptor.ClassAgnosticNms);
        var results = _decoder.Decode(outputs, _labels, thresholds, _profile);

        stopwatch.Stop();
        LastInferenceMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        if (!_loggedFirstInference)
        {
            _loggedFirstInference = true;
            _logger.LogInformation(
                "First descriptor-driven inference: {Ms:F1} ms (includes provider warmup).",
                LastInferenceMilliseconds);
        }

        return results;
    }

    /// <summary>Slice-mode entry point — see this class's own doc comment for the GPU-native-vs-
    /// CPU-per-tile decision, made once at construction (<see cref="_gpuNativeSlicing"/>). One nv12
    /// buffer, the whole <see cref="SliceLayout.CaptureWidth"/>×<see cref="SliceLayout.CaptureHeight"/>
    /// frame, in; one detection list per slice, in <see cref="SliceLayout.Slices"/> order, out.</summary>
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

        // No per-call iou parameter on this interface (see ISlicedDetectionEngine's own doc comment) —
        // the descriptor's own configured IouThreshold is what each slice's own within-slice NMS uses;
        // SliceMerge (the caller) handles cross-slice/seam dedup separately.
        var thresholds = new DecodeThresholds((float)confidence, (float)_descriptor.IouThreshold,
            _descriptor.MaxDetections, _descriptor.ClassAgnosticNms);

        var results = _gpuNativeSlicing
            ? DetectSlicedGpuNative(wholeFrameNv12, layout, thresholds)
            : DetectSlicedCpuPerTile(wholeFrameNv12, layout, thresholds);

        stopwatch.Stop();
        LastInferenceMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        if (!_loggedFirstInference)
        {
            _loggedFirstInference = true;
            _logger.LogInformation(
                "First descriptor-driven sliced inference: {Ms:F1} ms ({Slices} slices, {Mode}; includes provider warmup).",
                LastInferenceMilliseconds, layout.Slices.Count, _gpuNativeSlicing ? "GPU-native" : "CPU-per-tile");
        }

        return results;
    }

    /// <summary>One batched forward pass over the whole frame — the merged head's own Slice+Concat
    /// (built at construction, see <see cref="TryBuildGpuNativeSlicedSession"/>) already produced the
    /// <c>[N,…]</c> batch, so this only has to decode each of its N slots. Mirrors
    /// <see cref="DFineEngine.DetectSliced"/>'s own shape exactly.</summary>
    private List<List<ObjectDetection>> DetectSlicedGpuNative(byte[] wholeFrameNv12, SliceLayout layout, DecodeThresholds thresholds)
    {
        var nv12Tensor = new DenseTensor<byte>(wholeFrameNv12, [1, layout.CaptureHeight * 3 / 2, layout.CaptureWidth]);
        List<NamedOnnxValue> inputs = [NamedOnnxValue.CreateFromTensor("nv12", nv12Tensor)];

        using var outputs = _lease.Run(inputs);

        // Read the real batch dimension rather than trusting layout.Slices.Count — mirrors
        // DFineEngine.DetectSliced's own convention. Defense in depth: load-time verification
        // (TryBuildGpuNativeSlicedSession) already proved this matches for a synthetic frame, but a
        // real per-frame mismatch here means something is badly wrong and must not be silently
        // decoded as if it were correct.
        var n = outputs.First().AsTensor<float>().Dimensions[0];
        if (n != layout.Slices.Count)
            throw new InvalidOperationException(
                $"GPU-native batched Slice session produced {n} batch slot(s) but the slice layout has " +
                $"{layout.Slices.Count} — load-time verification should have caught this; treat this " +
                "engine as broken rather than decoding the wrong number of slices.");

        var results = new List<List<ObjectDetection>>(n);
        for (var slot = 0; slot < n; slot++)
            results.Add(_decoder.Decode(outputs, _labels, thresholds, _profile, slot));
        return results;
    }

    /// <summary>N separate batch-1 forward passes, one per cropped tile — see this class's own doc
    /// comment for why this is the default. Always correct regardless of the model's own graph.</summary>
    private List<List<ObjectDetection>> DetectSlicedCpuPerTile(byte[] wholeFrameNv12, SliceLayout layout, DecodeThresholds thresholds)
    {
        var results = new List<List<ObjectDetection>>(layout.Slices.Count);
        foreach (var tile in layout.Slices)
        {
            var input = NamedOnnxValue.CreateFromTensor(_inputName,
                PreprocessNv12Tile(wholeFrameNv12, layout.CaptureWidth, layout.CaptureHeight, tile.X, tile.Y, tile.Width, tile.Height));
            using var outputs = _lease.Run([input]);
            // batchSlot defaults to 0 — each Run here is its own single-image (batch-1) forward pass.
            results.Add(_decoder.Decode(outputs, _labels, thresholds, _profile));
        }
        return results;
    }

    /// <summary>True when the model's own declared input batch dimension isn't hardcoded to a literal
    /// 1 — ONNX Runtime reports a symbolic/dynamic dimension as -1 in <see cref="NodeMetadata.Dimensions"/>,
    /// which is what an Ultralytics <c>dynamic=True</c> export produces. Not proof the graph is
    /// actually safe to batch (only <see cref="TryBuildGpuNativeSlicedSession"/> actually finds that
    /// out) — just cheap enough to gate the attempt on, so a definitely-static model (every plain,
    /// non-dynamic export) never pays for one.</summary>
    private static bool DeclaresPossiblyDynamicBatch(InferenceSession session, string inputName)
    {
        var dims = session.InputMetadata[inputName].Dimensions;
        return dims.Length > 0 && dims[0] != 1;
    }

    /// <summary>Attempts the same GPU-native <see cref="OnnxPreprocessHead.MergeSliced"/> path
    /// <see cref="DFineEngine"/>/<see cref="YoloXEngine"/> use, returning null (never throwing) if the
    /// descriptor's normalize convention isn't one <c>MergeSliced</c> can represent, if ONNX Runtime
    /// rejects the merged graph outright, or — critically — if
    /// <see cref="VerifyBatchedOutputsMatchSingleImage"/> finds the accepted graph's own numbers don't
    /// actually agree with N independent single-tile runs. See this class's own doc comment for why
    /// "ONNX Runtime didn't throw" alone is not sufficient proof and was the cause of a real production
    /// regression.</summary>
    // Batched-graph variants that failed their check in this process, with why. Without this, every
    // camera on the same slice layout rebuilt and re-tested a graph already known to be unusable, and
    // each throw-away TensorRT session load left native memory the process didn't hand back. Keyed by
    // the session-pool key plus the model file's timestamp, so a replaced model file is tried afresh.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> FailedBatchedVariants = new();

    private static string? BatchedFailureKey(EngineOptions opts)
    {
        if (SharedSessionPool.KeyFor(opts) is not { } key) return null;
        try { return key + "|" + File.GetLastWriteTimeUtc(opts.ModelPath).Ticks; }
        catch (IOException) { return key; }
    }

    private SessionLease? TryBuildGpuNativeSlicedSession(EngineOptions options, SliceLayout sliceLayout,
        InferenceProfile profile, InferenceSession plainSession, ILogger<GenericOnnxEngine> logger)
    {
        if (!TryResolveMergedSliceNormalizeConvention(_descriptor.Normalize, out var rescaleTo01))
        {
            _logger.LogInformation(
                "Model {Path} declares a possibly-dynamic batch input, but its normalize convention " +
                "(scale {Scale}, mean {Mean}, std {Std}) isn't one of the two MergeSliced supports " +
                "(plain 0-1 rescale or raw 0-255, both requiring zero mean/unit std) — using the " +
                "CPU-per-tile path, which supports any normalize convention.",
                options.ModelPath, _descriptor.Normalize.Scale,
                string.Join(",", _descriptor.Normalize.Mean), string.Join(",", _descriptor.Normalize.Std));
            return null;
        }

        var opts = options with
        {
            TensorRtCacheKey = OrtSessionFactory.TensorRtCacheKeyFor(options.ModelPath, profile, sliceLayout,
                options.BatchSize, gpuPreprocessing: true),
        };
        var failureKey = BatchedFailureKey(opts);
        if (failureKey is not null && FailedBatchedVariants.TryGetValue(failureKey, out var earlierFailure))
        {
            _logger.LogInformation(
                "Model {Path}: GPU-native batched Slice graph for this slice layout already failed its check in this " +
                "process ({Reason}) — using the CPU-per-tile path without rebuilding it.",
                options.ModelPath, earlierFailure);
            return null;
        }

        SessionLease? candidate = null;
        try
        {
            candidate = SharedSessionPool.Acquire(SharedSessionPool.KeyFor(opts), options.MaxCamerasPerSession, () =>
            {
                var sessionOptions = OrtSessionFactory.Create(opts, logger);
                var merged = OnnxPreprocessHead.MergeSliced(File.ReadAllBytes(opts.ModelPath), sliceLayout, _inputName,
                    rescaleTo01, _channelOrderRgb);
                return new InferenceSession(merged, sessionOptions);
            });

            // A session already in the pool passed this same check when the camera that created it was
            // built (a failed one is disposed straight away, below, before any other build can run).
            if (candidate.IsShared) return candidate;

            if (!VerifyBatchedOutputsMatchSingleImage(candidate.Session, plainSession, sliceLayout, out var mismatchReason))
            {
                _logger.LogWarning(
                    "Model {Path}: GPU-native batched Slice graph loaded, but its output didn't " +
                    "numerically match {Count} separate single-tile runs on a synthetic verification " +
                    "frame ({Reason}) — falling back to the CPU-per-tile path. This means the model's " +
                    "batch handling is shape-valid but not actually correct (a confirmed real example: " +
                    "some export pipelines freeze a per-batch index at trace time, so every tile's " +
                    "detections come out carrying one tile's own content).",
                    options.ModelPath, sliceLayout.Slices.Count, mismatchReason);
                candidate.Dispose();
                if (failureKey is not null) FailedBatchedVariants[failureKey] = mismatchReason ?? "verification failed";
                return null;
            }

            _logger.LogInformation(
                "Model {Path}: GPU-native batched Slice mode built and numerically verified ({Count}-way " +
                "batch matches {Count} separate single-tile runs on a synthetic frame).",
                options.ModelPath, sliceLayout.Slices.Count, sliceLayout.Slices.Count);
            return candidate;
        }
        catch (Exception ex)
        {
            candidate?.Dispose();
            if (failureKey is not null) FailedBatchedVariants[failureKey] = ex.GetType().Name + ": " + ex.Message;
            _logger.LogWarning(ex,
                "Model {Path}: declared a possibly-dynamic batch input, but building or verifying the " +
                "GPU-native batched Slice graph failed — falling back to the CPU-per-tile path, which " +
                "works regardless of this model's own internal batch handling. If this model was " +
                "deliberately exported with a dynamic batch axis, this usually means some other op in " +
                "the graph still hardcodes batch=1 internally.",
                options.ModelPath);
            return null;
        }
    }

    /// <summary>The numerical self-check <see cref="TryBuildGpuNativeSlicedSession"/> exists to run:
    /// builds one deterministic, non-uniform synthetic nv12 frame (never sent to the model for real
    /// decoding), runs it once through <paramref name="batchedSession"/>'s merged graph, then crops
    /// and runs each of the same frame's tiles individually through <paramref name="plainSession"/> —
    /// the exact CPU-per-tile preprocessing path (<see cref="PreprocessNv12Tile"/>) this whole check
    /// exists to fall back to if verification fails. Every named output is compared per slot by
    /// <see cref="SlicedOutputComparison"/> (no decoder-specific knowledge, so this works for any
    /// decoder kind) — this is what actually proves the claim the class doc comment makes, which "ONNX
    /// Runtime didn't throw" alone does not. Content must vary across the synthetic frame (not a flat
    /// color): if the batched graph's per-slot output is actually frozen to one slot's content, a
    /// uniform frame would make every tile's comparison coincidentally match.</summary>
    private bool VerifyBatchedOutputsMatchSingleImage(InferenceSession batchedSession, InferenceSession plainSession,
        SliceLayout layout, out string? mismatchReason)
    {
        mismatchReason = null;
        var wholeFrame = BuildSyntheticNv12Frame(layout.CaptureWidth, layout.CaptureHeight);
        var n = layout.Slices.Count;

        var nv12Tensor = new DenseTensor<byte>(wholeFrame, [1, layout.CaptureHeight * 3 / 2, layout.CaptureWidth]);
        using var batchedOutputs = batchedSession.Run([NamedOnnxValue.CreateFromTensor("nv12", nv12Tensor)]);

        // Per output name: each tile's single-run values (copied — the run's own buffers are freed when
        // its outputs are disposed) and the output's per-image shape.
        var singles = new Dictionary<string, (float[][] Slots, int[] Dims)>();
        for (var i = 0; i < n; i++)
        {
            var tile = layout.Slices[i];
            var singleInput = NamedOnnxValue.CreateFromTensor(_inputName,
                PreprocessNv12Tile(wholeFrame, layout.CaptureWidth, layout.CaptureHeight, tile.X, tile.Y, tile.Width, tile.Height));

            using var singleOutputs = plainSession.Run([singleInput]);
            foreach (var singleOutput in singleOutputs)
            {
                var tensor = singleOutput.AsTensor<float>();
                if (!singles.TryGetValue(singleOutput.Name, out var entry))
                    singles[singleOutput.Name] = entry = (new float[n][], tensor.Dimensions.ToArray());
                entry.Slots[i] = tensor.ToArray();
            }
        }

        foreach (var (name, (singleSlots, singleDims)) in singles)
        {
            var batchedOutput = batchedOutputs.FirstOrDefault(o => o.Name == name);
            if (batchedOutput is null)
            {
                mismatchReason = $"batched graph has no output named '{name}'";
                return false;
            }

            var batchedTensor = batchedOutput.AsTensor<float>();
            var batchedDims = batchedTensor.Dimensions;
            if (batchedDims.Length != singleDims.Length || batchedDims[0] != n)
            {
                mismatchReason = $"output '{name}' batch shape mismatch " +
                    $"(batched [{string.Join(",", batchedDims.ToArray())}], expected {n} " +
                    $"in dim 0 to match single [{string.Join(",", singleDims)}])";
                return false;
            }

            var perImage = 1;
            for (var d = 1; d < singleDims.Length; d++)
            {
                if (batchedDims[d] != singleDims[d])
                {
                    mismatchReason = $"output '{name}' per-image shape mismatch at dim {d} " +
                        $"(batched {batchedDims[d]} vs single {singleDims[d]})";
                    return false;
                }
                perImage *= singleDims[d];
            }

            var batchedAll = batchedTensor.ToArray();
            var batchedSlots = new float[n][];
            for (var i = 0; i < n; i++) batchedSlots[i] = batchedAll.AsSpan(i * perImage, perImage).ToArray();

            var rowWidth = singleDims.Length > 1 ? singleDims[^1] : perImage;
            mismatchReason = SlicedOutputComparison.Compare(name, batchedSlots, singleSlots, rowWidth, out var identitySkipped);
            if (mismatchReason is not null) return false;
            // Unproven is a failure, not a pass. Closeness alone let through a graph that returned
            // detections for slot 0 only (every other slot empty) on real frames, because the synthetic
            // frame's single-tile outputs were too alike to tell the slots apart.
            if (identitySkipped)
            {
                mismatchReason = $"output '{name}' came out nearly the same for every tile of the synthetic frame, so it " +
                    "can't show whether each slot carries its own tile";
                return false;
            }
        }

        return true;
    }

    /// <summary>Deterministic but non-uniform synthetic nv12 frame for verification only — a plain
    /// coordinate-based gradient, not real camera content. Never decoded for real detections.</summary>
    private static byte[] BuildSyntheticNv12Frame(int width, int height)
    {
        var frame = new byte[width * height * 3 / 2];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
                frame[y * width + x] = (byte)((x * 5 + y * 7) % 256);
        }

        var uvStart = width * height;
        for (var i = uvStart; i < frame.Length; i++)
            frame[i] = (byte)(128 + (i * 3) % 64 - 32);

        return frame;
    }

    /// <summary>Maps a descriptor's general <c>value = (channel*scale - mean) / std</c> normalize spec
    /// onto <see cref="OnnxPreprocessHead.MergeSliced"/>'s only two supported conventions — a plain
    /// boolean, not general mean/std (see that method's own doc comment). Requires zero mean and unit
    /// std for every channel; <paramref name="rescaleTo01"/> is then just which of the two remaining
    /// scale values the descriptor asked for. Anything else (any real per-channel normalize) returns
    /// false — <see cref="Preprocess"/>'s own CPU path supports the general case already, this is only
    /// about whether the GPU-native shortcut can represent it too.</summary>
    private static bool TryResolveMergedSliceNormalizeConvention(NormalizeSpec normalize, out bool rescaleTo01)
    {
        const double tolerance = 1e-6;
        rescaleTo01 = false;

        if (normalize.Mean.Count != 3 || normalize.Std.Count != 3) return false;
        if (normalize.Mean.Any(m => Math.Abs(m) > tolerance)) return false;
        if (normalize.Std.Any(s => Math.Abs(s - 1.0) > tolerance)) return false;

        if (Math.Abs(normalize.Scale - 1.0 / 255.0) < tolerance) { rescaleTo01 = true; return true; }
        if (Math.Abs(normalize.Scale - 1.0) < tolerance) { rescaleTo01 = false; return true; }
        return false;
    }

    /// <summary>CPU-per-tile preprocessing: one tile of a whole nv12 frame, converted (BT.601 limited
    /// range, matching every other nv12 consumer in this codebase) and normalized straight into the
    /// reused <see cref="_inputTensor"/>. Same values as cropping the tile to its own nv12 buffer,
    /// converting that to BGRA, then <see cref="Preprocess"/> — which is what this replaced: those two
    /// intermediate buffers were fresh Large Object Heap arrays (~0.6 MB + ~1.6 MB) per tile per frame,
    /// ~150 MB/s on a 7-camera recorder, and the cause of its ~4 background gen2 GCs a second. Tile
    /// origins are even (see <see cref="SliceLayout"/>), so each pixel reads the same chroma pair the
    /// crop would have.</summary>
    private DenseTensor<float> PreprocessNv12Tile(byte[] frame, int frameW, int frameH, int tileX, int tileY, int tileW, int tileH)
    {
        if (tileW != _profile.NetworkWidth || tileH != _profile.NetworkHeight)
            throw new InvalidOperationException(
                $"Slice tile is {tileW}x{tileH}, expected the network size {_profile.NetworkWidth}x{_profile.NetworkHeight}.");

        var n = _descriptor.Normalize;
        var scale = n.Scale;
        var (mean0, mean1, mean2) = (n.Mean[0], n.Mean[1], n.Mean[2]);
        var (std0, std1, std2) = (n.Std[0], n.Std[1], n.Std[2]);

        var t = _inputTensor.Buffer.Span;
        var pixelCount = tileW * tileH;
        var plane1 = pixelCount;
        var plane2 = pixelCount * 2;
        var srcUv = frameW * frameH;

        for (var y = 0; y < tileH; y++)
        {
            var sy = tileY + y;
            var yRow = sy * frameW;
            var cyRow = srcUv + (sy / 2) * frameW;
            for (var x = 0; x < tileW; x++)
            {
                var sx = tileX + x;
                var cxByte = (sx / 2) * 2;
                var (r, g, b) = Bt601Limited.ToRgb(frame[yRow + sx], frame[cyRow + cxByte], frame[cyRow + cxByte + 1]);

                // Same plane/normalize rules as Preprocess.
                var (val0, val2) = _channelOrderRgb ? (r, b) : (b, r);
                var i = y * tileW + x;
                t[i] = (float)((val0 * scale - mean0) / std0);
                t[plane1 + i] = (float)((g * scale - mean1) / std1);
                t[plane2 + i] = (float)((val2 * scale - mean2) / std2);
            }
        }

        return _inputTensor;
    }

    /// <summary>Packs one BGRA8888 frame into a <c>[1,3,H,W]</c> float32 NCHW tensor per the
    /// descriptor's own <see cref="ModelDescriptor.ChannelOrder"/> and
    /// <see cref="ModelDescriptor.Normalize"/> (<c>value = channel*scale - mean) / std</c>, per
    /// channel) — generic over whatever a dropped-in model actually needs, unlike
    /// <see cref="DFineEngine"/>/<see cref="YoloXEngine"/>'s own hardcoded, single-model-verified
    /// constants. Shared by both <see cref="Detect"/> (a captured frame) and the CPU-per-tile
    /// <see cref="DetectSlicedCpuPerTile"/> (one tile, already cropped to the same network size). Not
    /// used by the GPU-native Slice path — that path's normalize runs inside the merged graph instead.</summary>
    private DenseTensor<float> Preprocess(byte[] bgra)
    {
        var pixelCount = _profile.NetworkWidth * _profile.NetworkHeight;
        if (bgra.Length != pixelCount * 4)
            throw new InvalidOperationException(
                $"Captured BGRA frame is {bgra.Length} bytes, expected {pixelCount * 4} for " +
                $"{_profile.NetworkWidth}x{_profile.NetworkHeight}.");

        var n = _descriptor.Normalize;
        var scale = n.Scale;
        var (mean0, mean1, mean2) = (n.Mean[0], n.Mean[1], n.Mean[2]);
        var (std0, std1, std2) = (n.Std[0], n.Std[1], n.Std[2]);

        var t = _inputTensor.Buffer.Span;
        var plane1 = pixelCount;
        var plane2 = pixelCount * 2;

        for (var i = 0; i < pixelCount; i++)
        {
            var o = i * 4; // BGRA byte order
            var b = bgra[o];
            var g = bgra[o + 1];
            var r = bgra[o + 2];

            // Mean/std are indexed by tensor-plane position (0/1/2), same as ChannelOrder itself —
            // plane 1 is always G either way; only which of R/B sits at plane 0 vs plane 2 swaps.
            var (val0, val2) = _channelOrderRgb ? (r, b) : (b, r);

            t[i] = (float)((val0 * scale - mean0) / std0);
            t[plane1 + i] = (float)((g * scale - mean1) / std1);
            t[plane2 + i] = (float)((val2 * scale - mean2) / std2);
        }

        return _inputTensor;
    }

    private void ValidateInputShape()
    {
        var dims = _lease.Session.InputMetadata[_inputName].Dimensions;
        if (dims.Length != 4)
        {
            _logger.LogWarning("Model input '{Input}' has rank {Rank}, expected 4 ([1,3,H,W]) — proceeding anyway.", _inputName, dims.Length);
            return;
        }

        var h = dims[2];
        var w = dims[3];
        if (h > 0 && w > 0 && (h != _profile.NetworkHeight || w != _profile.NetworkWidth))
            throw new InvalidOperationException(
                $"Model expects a fixed {w}x{h} input but the pipeline is decoding frames at " +
                $"{_profile.NetworkWidth}x{_profile.NetworkHeight} — check the descriptor's 'inputSize'.");
    }

    public void Dispose() => _lease.Dispose();
}
