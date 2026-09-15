using System.Buffers;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using YoloDotNet.Models;

namespace LarisVMS.Vision.Inference.Decoders;

/// <summary>
/// Decodes an Ultralytics YOLOv8 / YOLO11 detect head. Output is <c>[B, 4+nc, N]</c> (attribute-major,
/// the default export) or its transpose <c>[B, N, 4+nc]</c> — disambiguated by matching <c>4+nc</c>
/// against the label count. Row 0-3 are <c>cx,cy,w,h</c> in input-size pixels (assumed square, equal
/// to <see cref="InferenceProfile.NetworkWidth"/>/<see cref="InferenceProfile.NetworkHeight"/> — the
/// descriptor-driven engine builds its profile from the same <c>inputSize</c> the model was described
/// with); the remaining <c>nc</c> rows are per-class scores already passed through sigmoid by the
/// export. There is no objectness channel. Class-aware greedy NMS is applied here (class-agnostic when
/// the descriptor asks for it).
///
/// Ported from SideGlance's own <c>Inference.Decoders.UltralyticsDecoder</c>, adapted to decode into
/// <see cref="ObjectDetection"/> (via <see cref="InferenceProfile.MapBoxToSource"/>) instead of
/// SideGlance's own tile-pixel-space <c>Detection</c> type, and to take the whole named output
/// collection (only one output is used) to match <see cref="IDetectionDecoder"/>'s shared shape.
/// </summary>
public sealed class UltralyticsDecoder : IDetectionDecoder
{
    public List<ObjectDetection> Decode(IReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        IReadOnlyList<string> labels, DecodeThresholds thresholds, InferenceProfile profile, int batchSlot = 0)
    {
        var outTensor = outputs.First().AsTensor<float>();
        var dims = outTensor.Dimensions;
        if (dims.Length != 3)
            throw new DecoderShapeException($"Ultralytics decoder expects a rank-3 output, got rank {dims.Length}.");

        var nc = labels.Count;
        var attrs = 4 + nc;
        var netW = profile.NetworkWidth;
        var netH = profile.NetworkHeight;

        bool attrMajor;
        int anchors;
        if (dims[1] == attrs) { attrMajor = true; anchors = dims[2]; }
        else if (dims[2] == attrs) { attrMajor = false; anchors = dims[1]; }
        else
            throw new DecoderShapeException(
                $"neither output dim matches 4+labelCount ({attrs}); dims were [{dims[0]},{dims[1]},{dims[2]}] " +
                $"for {nc} labels.");

        ReadOnlySpan<float> output = outTensor is DenseTensor<float> dense ? dense.Buffer.Span : outTensor.ToArray();
        var conf = thresholds.Confidence;

        // attribute-major packs [attr][n] (stride `anchors` between attributes, 1 between anchors);
        // anchor-major packs [n][attr] (stride `attrs` between anchors, 1 between attributes).
        var attrStride = attrMajor ? anchors : 1;
        var anchorStride = attrMajor ? 1 : attrs;
        // Offset into `output` for this batch slot — each slot occupies attrs*anchors floats
        // regardless of attribute-/anchor-major layout. 0 for a plain single-frame call (the only
        // case before Slice-mode support); a real offset when GenericOnnxEngine's Slice-mode path
        // calls this once per slice against one shared batched forward pass.
        var slotBase = batchSlot * attrs * anchors;

        var bestScoreRented = ArrayPool<float>.Shared.Rent(anchors);
        var bestClassRented = ArrayPool<int>.Shared.Rent(anchors);
        try
        {
            var bestScore = bestScoreRented.AsSpan(0, anchors);
            var bestClass = bestClassRented.AsSpan(0, anchors);

            if (attrMajor)
            {
                var classBase = slotBase + 4 * attrStride;
                for (var n = 0; n < anchors; n++) { bestScore[n] = output[classBase + n]; bestClass[n] = 0; }

                for (var c = 1; c < nc; c++)
                {
                    classBase = slotBase + (4 + c) * attrStride;
                    for (var n = 0; n < anchors; n++)
                    {
                        var s = output[classBase + n];
                        if (s > bestScore[n]) { bestScore[n] = s; bestClass[n] = c; }
                    }
                }
            }
            else
            {
                for (var n = 0; n < anchors; n++)
                {
                    var rowBase = slotBase + n * anchorStride;
                    var best = output[rowBase + 4];
                    var cls = 0;
                    for (var c = 1; c < nc; c++)
                    {
                        var s = output[rowBase + 4 + c];
                        if (s > best) { best = s; cls = c; }
                    }
                    bestScore[n] = best;
                    bestClass[n] = cls;
                }
            }

            var candidates = new List<(double Cx, double Cy, double W, double H, int Class, double Score)>();
            for (var n = 0; n < anchors; n++)
            {
                if (bestScore[n] < conf) continue;

                var rowBase = slotBase + n * anchorStride;
                var cx = output[rowBase] / netW;
                var cy = output[rowBase + attrStride] / netH;
                var w = output[rowBase + 2 * attrStride] / netW;
                var h = output[rowBase + 3 * attrStride] / netH;
                candidates.Add((cx, cy, w, h, bestClass[n], bestScore[n]));
            }

            return SuppressAndMap(candidates, labels, thresholds, profile);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(bestScoreRented);
            ArrayPool<int>.Shared.Return(bestClassRented);
        }
    }

    /// <summary>Greedy NMS over normalized (0-1) cxcywh candidates — class-aware unless
    /// <see cref="DecodeThresholds.ClassAgnosticNms"/> is set (mirrors YoloXDecoder's own per-class
    /// suppression loop, the established LarisVMS-native pattern for a dense-grid head; the shared,
    /// always-class-blind <see cref="Nms"/> class is for whole-frame/sliced dedup, a different use).
    /// Maps surviving candidates to source-frame coordinates via <see cref="InferenceProfile.MapBoxToSource"/>.</summary>
    private static List<ObjectDetection> SuppressAndMap(
        List<(double Cx, double Cy, double W, double H, int Class, double Score)> candidates,
        IReadOnlyList<string> labels, DecodeThresholds thresholds, InferenceProfile profile)
    {
        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        var suppressed = new bool[candidates.Count];
        var results = new List<ObjectDetection>();
        for (var i = 0; i < candidates.Count && results.Count < thresholds.MaxDetections; i++)
        {
            if (suppressed[i]) continue;
            var kept = candidates[i];

            for (var j = i + 1; j < candidates.Count; j++)
            {
                if (suppressed[j]) continue;
                if (!thresholds.ClassAgnosticNms && candidates[j].Class != kept.Class) continue;
                if (BoxIoU(kept, candidates[j]) >= thresholds.Iou) suppressed[j] = true;
            }

            var (nx0, ny0, nx1, ny1) = profile.MapBoxToSource(kept.Cx, kept.Cy, kept.W, kept.H);
            var left = (int)Math.Round(Math.Clamp(nx0 * profile.SourceWidth, 0, profile.SourceWidth));
            var top = (int)Math.Round(Math.Clamp(ny0 * profile.SourceHeight, 0, profile.SourceHeight));
            var right = (int)Math.Round(Math.Clamp(nx1 * profile.SourceWidth, 0, profile.SourceWidth));
            var bottom = (int)Math.Round(Math.Clamp(ny1 * profile.SourceHeight, 0, profile.SourceHeight));
            if (right <= left || bottom <= top) continue;

            results.Add(new ObjectDetection
            {
                Label = new LabelModel { Index = kept.Class, Name = labels[kept.Class] },
                Confidence = kept.Score,
                BoundingBox = new SKRectI(left, top, right, bottom),
                Tail = [],
            });
        }

        return results;
    }

    private static double BoxIoU((double Cx, double Cy, double W, double H, int Class, double Score) a,
        (double Cx, double Cy, double W, double H, int Class, double Score) b)
    {
        double ax0 = a.Cx - a.W / 2, ay0 = a.Cy - a.H / 2, ax1 = a.Cx + a.W / 2, ay1 = a.Cy + a.H / 2;
        double bx0 = b.Cx - b.W / 2, by0 = b.Cy - b.H / 2, bx1 = b.Cx + b.W / 2, by1 = b.Cy + b.H / 2;

        var ix = Math.Max(0, Math.Min(ax1, bx1) - Math.Max(ax0, bx0));
        var iy = Math.Max(0, Math.Min(ay1, by1) - Math.Max(ay0, by0));
        var inter = ix * iy;
        if (inter <= 0) return 0;

        var union = a.W * a.H + b.W * b.H - inter;
        return union <= 0 ? 0 : inter / union;
    }
}

/// <summary>Thrown when a model's output tensor doesn't match the shape the chosen decoder expects.
/// Raised at load-time warm-up (so the model is marked failed, not silently miscounted) or on the
/// first request. Ported from SideGlance's own <c>DecoderShapeException</c>.</summary>
public sealed class DecoderShapeException(string message) : Exception(message);
