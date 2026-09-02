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
/// </summary>
public sealed class YoloXEngine : IDetectionEngine
{
    /// <summary>Megvii's own <c>preproc</c> keeps cv2's BGR order (no <c>[:, :, ::-1]</c>). Set true
    /// only if a re-pinned export expects RGB.</summary>
    private const bool ChannelOrderRgb = false;

    private readonly InferenceSession _session;
    private readonly InferenceProfile _profile;
    private readonly IReadOnlyList<string> _labels;
    private readonly ILogger<YoloXEngine> _logger;
    private readonly string _inputName;
    private bool _loggedFirstInference;

    public YoloXEngine(EngineOptions options, InferenceProfile profile, IReadOnlyList<string> labels, ILogger<YoloXEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(labels);
        _profile = profile;
        _labels = labels;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var stopwatch = Stopwatch.StartNew();
        _session = new InferenceSession(options.ModelPath, OrtSessionFactory.Create(options, logger));
        stopwatch.Stop();

        _inputName = _session.InputMetadata.Keys.First();
        ValidateInputShape();

        _logger.LogInformation(
            "Loaded YOLOX model ({Classes} classes, input '{Input}') from {Path} in {LoadMs} ms.",
            _labels.Count, _inputName, options.ModelPath, stopwatch.ElapsedMilliseconds);
    }

    public double? LastInferenceMilliseconds { get; private set; }

    public List<ObjectDetection> Detect(byte[] frame, double confidence = 0.25, double iou = 0.5)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var stopwatch = Stopwatch.StartNew();

        var input = NamedOnnxValue.CreateFromTensor(_inputName, Preprocess(frame));
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
        var results = YoloXDecoder.Decode(outTensor.ToArray(), numAnchors, numAttrs, _labels, confidence, iou, _profile);

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

    private DenseTensor<float> Preprocess(byte[] bgra)
    {
        var pixelCount = _profile.NetworkWidth * _profile.NetworkHeight;
        if (bgra.Length != pixelCount * 4)
            throw new InvalidOperationException(
                $"Captured BGRA frame is {bgra.Length} bytes, expected {pixelCount * 4} for " +
                $"{_profile.NetworkWidth}x{_profile.NetworkHeight} — VisionSession's ffmpeg filter chain and this " +
                "engine's InferenceProfile have disagreed about the decode target.");

        var tensor = new DenseTensor<float>([1, 3, _profile.NetworkHeight, _profile.NetworkWidth]);
        var t = tensor.Buffer.Span; // planar: [plane0][plane1][plane2], each H*W
        var plane1 = pixelCount;
        var plane2 = pixelCount * 2;

        // BGRA byte order is B,G,R,A. Planar output is RGB when ChannelOrderRgb, else BGR — no /255.
        var rPlane = ChannelOrderRgb ? 0 : plane2;
        var bPlane = ChannelOrderRgb ? plane2 : 0;

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
