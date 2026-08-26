using SkiaSharp;
using YoloDotNet.Models;

namespace LarisVMS.Vision.Inference;

/// <summary>
/// Decodes D-FINE's raw ONNX output (`logits [1,300,C]`, `pred_boxes [1,300,4]`) into
/// YoloDotNet's own <see cref="ObjectDetection"/> shape, so everything downstream of inference
/// (ByteTracker, MovementClassifier, CameraDetectionPipeline, CocoCategoryMap) keeps working
/// completely unchanged — see DFineEngine's own doc comment for why this whole class exists
/// instead of routing D-FINE through YoloDotNet's own decoder.
///
/// A pure function (tensors in, detections out), independently verified against a real model run
/// during the D-FINE integration's design phase: manually replicating this exact math (sigmoid,
/// flatten, threshold, cxcywh→xyxy) against `onnx-community/dfine_s_obj2coco-ONNX` on the classic
/// COCO "two cats + remote controls" test image (000000039769.jpg) correctly recovered both cats,
/// the couch, and both remotes with clean, non-duplicated boxes — confirming no NMS step is needed
/// (DETR-style: one query per object, not a dense anchor grid) and that this decode logic matches
/// the model's actual behavior, not just a plausible-looking guess at it.
/// </summary>
public static class DFineDecoder
{
    private static double Sigmoid(float x) => 1.0 / (1.0 + Math.Exp(-x));

    /// <summary>
    /// Decodes one frame's raw output. <paramref name="logits"/> is <paramref name="numQueries"/> *
    /// <paramref name="numClasses"/> raw (pre-sigmoid) scores, row-major by query; <paramref
    /// name="boxes"/> is <paramref name="numQueries"/> * 4 normalized (0-1) cxcywh values, row-major
    /// by query — the exact layout `logits`/`pred_boxes` come out of the ONNX session in.
    ///
    /// <paramref name="labels"/> is index-matched to the class dimension (DFineLabels.Obj2Coco or
    /// .Obj365) — a class whose label is exactly "None" (Objects365's index-0 padding slot) is
    /// always skipped, never reported as a real detection.
    ///
    /// No NMS: D-FINE is DETR-style (one query per real object, not a dense per-pixel anchor grid),
    /// so unlike YOLO's raw output this never needs suppression to collapse duplicate boxes.
    /// </summary>
    public static List<ObjectDetection> Decode(
        ReadOnlySpan<float> logits, ReadOnlySpan<float> boxes,
        int numQueries, int numClasses, IReadOnlyList<string> labels,
        double confidenceThreshold, int frameWidth, int frameHeight, int maxDetections = 100)
    {
        if (logits.Length != numQueries * numClasses)
            throw new ArgumentException($"logits length {logits.Length} != numQueries*numClasses ({numQueries}*{numClasses}).", nameof(logits));
        if (boxes.Length != numQueries * 4)
            throw new ArgumentException($"boxes length {boxes.Length} != numQueries*4 ({numQueries}*4).", nameof(boxes));
        if (labels.Count != numClasses)
            throw new ArgumentException($"labels.Count {labels.Count} != numClasses ({numClasses}).", nameof(labels));

        // Every (query, class) candidate above threshold, ranked by score — mirrors the reference
        // HF postprocessor's own "flatten queries*classes, take the global top scorers" approach
        // (verified against it directly, see this class's own doc comment), which is deliberately
        // not "one class per query": a query landing near a real boundary between two plausible
        // classes can legitimately clear the threshold for both, and dropping the second would just
        // be a different, unverified guess at the model's own behavior.
        //
        // Pre-filtered in logit space before ever calling Sigmoid (Math.Exp): sigmoid is monotonic,
        // so `sigmoid(x) >= threshold` iff `x >= logit(threshold)` — a plain float comparison. The
        // overwhelming majority of the numQueries*numClasses grid is background (obj365's 366
        // classes means 109,800 candidates *per frame*, almost all of them nowhere near the
        // threshold), and Math.Exp is expensive enough that computing it unconditionally for the
        // whole grid was the dominant CPU cost of the entire pipeline — confirmed live once this
        // shipped without the pre-filter (real-hardware CPU usage went up, not down, despite D-FINE
        // itself being lighter than YOLO's own decode+NMS). Numerically identical result, since the
        // comparison is exact for every finite logit; only the arithmetic used to reach it is cheaper.
        var logitThreshold = (float)Math.Log(confidenceThreshold / (1 - confidenceThreshold));
        var candidates = new List<(int Query, int Class, double Score)>();
        for (var q = 0; q < numQueries; q++)
        {
            var rowStart = q * numClasses;
            for (var c = 0; c < numClasses; c++)
            {
                var rawLogit = logits[rowStart + c];
                if (rawLogit < logitThreshold) continue;
                candidates.Add((q, c, Sigmoid(rawLogit)));
            }
        }
        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        var results = new List<ObjectDetection>(Math.Min(candidates.Count, maxDetections));
        foreach (var (query, cls, score) in candidates)
        {
            if (results.Count >= maxDetections) break;

            var label = labels[cls];
            if (label == "None") continue; // Objects365's padding slot — not a real class.

            var boxStart = query * 4;
            var cx = boxes[boxStart];
            var cy = boxes[boxStart + 1];
            var w = boxes[boxStart + 2];
            var h = boxes[boxStart + 3];

            var x0 = (cx - w / 2f) * frameWidth;
            var y0 = (cy - h / 2f) * frameHeight;
            var x1 = (cx + w / 2f) * frameWidth;
            var y1 = (cy + h / 2f) * frameHeight;

            // Clamp to frame bounds — a box near the edge of a query's own field can extend
            // slightly past it, and nothing downstream expects a negative or out-of-frame SKRectI.
            var left = (int)Math.Round(Math.Clamp(x0, 0, frameWidth));
            var top = (int)Math.Round(Math.Clamp(y0, 0, frameHeight));
            var right = (int)Math.Round(Math.Clamp(x1, 0, frameWidth));
            var bottom = (int)Math.Round(Math.Clamp(y1, 0, frameHeight));
            if (right <= left || bottom <= top) continue; // degenerate box — nothing to report.

            results.Add(new ObjectDetection
            {
                Label = new LabelModel { Index = cls, Name = label },
                Confidence = score,
                BoundingBox = new SKRectI(left, top, right, bottom),
                Tail = [],
            });
        }

        return results;
    }
}
