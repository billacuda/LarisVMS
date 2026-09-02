using SkiaSharp;
using YoloDotNet.Models;

namespace LarisVMS.Vision.Inference;

/// <summary>
/// Decodes YOLOX's ONNX output into YoloDotNet's <see cref="ObjectDetection"/> shape, so everything
/// downstream of inference (ByteTracker, MovementClassifier, CameraDetectionPipeline, CocoCategoryMap)
/// works unchanged — the same role <see cref="DFineDecoder"/> plays for D-FINE.
///
/// <b>Model contract</b> — a single output tensor <c>[1, N, 5 + numClasses]</c> (N ≈ 8400 at 640,
/// 3549 at 416), the concatenated flattened heads. Per row: 4 box values, objectness, one score per
/// class. Objectness and class scores are always already sigmoid'd by the export. The 4 box values
/// are one of two forms and this decoder detects which:
/// <list type="bullet">
///   <item><b>raw</b> (Megvii's own <c>onnx_inference.py</c> contract, what <c>fetch_yolox.py</c>
///     pins): cols 0-1 are grid-cell offsets, cols 2-3 are log sizes — decoded here as
///     <c>(off + grid) * stride</c> and <c>exp(size) * stride</c> for strides 8/16/32.</item>
///   <item><b>pre-decoded</b> (OpenCV Model Zoo etc.): cols 0-3 are already <c>cxcywh</c> in
///     network-input pixels.</item>
/// </list>
/// NMS is never in the graph — YOLOX post-processing does per-class NMS, done here.
/// </summary>
public static class YoloXDecoder
{
    private static readonly int[] Strides = [8, 16, 32];

    private readonly record struct Candidate(double Cx, double Cy, double W, double H, int Class, double Score);

    /// <summary>Decodes one frame's output. <paramref name="output"/> is the flattened
    /// <c>[1, numAnchors, numAttrs]</c> tensor (row-major by anchor); <paramref name="numAttrs"/> is
    /// <c>5 + numClasses</c>. Boxes come back in <see cref="InferenceProfile.SourceWidth"/> ×
    /// <see cref="InferenceProfile.SourceHeight"/> pixel space, same as <see cref="DFineDecoder"/>.</summary>
    public static List<ObjectDetection> Decode(
        ReadOnlySpan<float> output, int numAnchors, int numAttrs, IReadOnlyList<string> labels,
        double confidenceThreshold, double iouThreshold, InferenceProfile profile, int maxDetections = 100)
    {
        var numClasses = numAttrs - 5;
        if (numClasses <= 0)
            throw new ArgumentException($"numAttrs {numAttrs} implies {numClasses} classes.", nameof(numAttrs));
        if (labels.Count != numClasses)
            throw new ArgumentException($"labels.Count {labels.Count} != numClasses ({numClasses}).", nameof(labels));
        if (output.Length != (long)numAnchors * numAttrs)
            throw new ArgumentException($"output length {output.Length} != numAnchors*numAttrs ({numAnchors}*{numAttrs}).", nameof(output));

        int netW = profile.NetworkWidth, netH = profile.NetworkHeight;

        // Raw box columns are small (grid offsets ≈ ±2, log sizes ≈ ±5); a pre-decoded model's are
        // pixels (0..netW). The gap is enormous, so one cheap pass over the box columns tells them
        // apart with no ambiguity.
        double maxAbsBox = 0;
        for (var a = 0; a < numAnchors; a++)
        {
            var b = a * numAttrs;
            for (var k = 0; k < 4; k++)
            {
                var v = Math.Abs(output[b + k]);
                if (v > maxAbsBox) maxAbsBox = v;
            }
        }
        var preDecoded = maxAbsBox > netW * 0.6;

        var conf = (float)confidenceThreshold;
        var candidates = new List<Candidate>();
        var grid = preDecoded ? null : BuildGrid(netW, netH, numAnchors);

        for (var a = 0; a < numAnchors; a++)
        {
            var b = a * numAttrs;
            var objectness = output[b + 4];
            // classScore ≤ 1, so objectness*classScore ≤ objectness — a row whose objectness is
            // already below the threshold can never produce a hit. Skips the class scan for the
            // overwhelming-background majority of anchors.
            if (objectness < conf) continue;

            var bestClass = 0;
            var bestClassScore = output[b + 5];
            for (var c = 1; c < numClasses; c++)
            {
                var s = output[b + 5 + c];
                if (s > bestClassScore) { bestClassScore = s; bestClass = c; }
            }

            var score = objectness * bestClassScore;
            if (score < conf) continue;

            double cxPx, cyPx, wPx, hPx;
            if (preDecoded)
            {
                cxPx = output[b + 0];
                cyPx = output[b + 1];
                wPx = output[b + 2];
                hPx = output[b + 3];
            }
            else
            {
                var (stride, gx, gy) = grid![a];
                cxPx = (output[b + 0] + gx) * stride;
                cyPx = (output[b + 1] + gy) * stride;
                wPx = Math.Exp(output[b + 2]) * stride;
                hPx = Math.Exp(output[b + 3]) * stride;
            }

            // Network pixels → normalized 0-1 for MapBoxToSource (which multiplies by NetworkWidth/Height).
            candidates.Add(new Candidate(cxPx / netW, cyPx / netH, wPx / netW, hPx / netH, bestClass, score));
        }

        candidates.Sort((x, y) => y.Score.CompareTo(x.Score));

        // Per-class greedy NMS — standard YOLOX post-processing. A "car" box never suppresses an
        // overlapping "person" box (that's why the shared, class-blind Nms.Suppress isn't reused here).
        var suppressed = new bool[candidates.Count];
        var kept = new List<Candidate>();
        for (var i = 0; i < candidates.Count && kept.Count < maxDetections; i++)
        {
            if (suppressed[i]) continue;
            kept.Add(candidates[i]);
            for (var j = i + 1; j < candidates.Count; j++)
            {
                if (suppressed[j] || candidates[j].Class != candidates[i].Class) continue;
                if (IoU(candidates[i], candidates[j]) >= iouThreshold) suppressed[j] = true;
            }
        }

        var results = new List<ObjectDetection>(kept.Count);
        foreach (var k in kept)
        {
            var (nx0, ny0, nx1, ny1) = profile.MapBoxToSource(k.Cx, k.Cy, k.W, k.H);
            var left = (int)Math.Round(Math.Clamp(nx0 * profile.SourceWidth, 0, profile.SourceWidth));
            var top = (int)Math.Round(Math.Clamp(ny0 * profile.SourceHeight, 0, profile.SourceHeight));
            var right = (int)Math.Round(Math.Clamp(nx1 * profile.SourceWidth, 0, profile.SourceWidth));
            var bottom = (int)Math.Round(Math.Clamp(ny1 * profile.SourceHeight, 0, profile.SourceHeight));
            if (right <= left || bottom <= top) continue;

            results.Add(new ObjectDetection
            {
                Label = new LabelModel { Index = k.Class, Name = labels[k.Class] },
                Confidence = k.Score,
                BoundingBox = new SKRectI(left, top, right, bottom),
                Tail = [],
            });
        }

        return results;
    }

    /// <summary>The (stride, gridX, gridY) for each flat anchor index — YOLOX concatenates its heads
    /// in stride order (8 then 16 then 32), each head row-major (y outer, x inner), matching
    /// Megvii's own <c>demo_postprocess</c>. If the anchor count doesn't match the expected
    /// 8/16/32 layout for this network size (a p6 model, say) the table is filled best-effort with
    /// the finest stride and the caller's boxes will be off — validated loudly enough elsewhere.</summary>
    private static (int Stride, int Gx, int Gy)[] BuildGrid(int netW, int netH, int numAnchors)
    {
        var table = new (int, int, int)[numAnchors];
        var idx = 0;
        foreach (var stride in Strides)
        {
            int wsize = netW / stride, hsize = netH / stride;
            for (var gy = 0; gy < hsize && idx < numAnchors; gy++)
            for (var gx = 0; gx < wsize && idx < numAnchors; gx++)
                table[idx++] = (stride, gx, gy);
        }
        while (idx < numAnchors) table[idx++] = (Strides[^1], 0, 0);
        return table;
    }

    private static double IoU(Candidate a, Candidate b)
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
