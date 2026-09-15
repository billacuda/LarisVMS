using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using YoloDotNet.Models;

namespace LarisVMS.Vision.Inference.Decoders;

/// <summary>
/// Decodes an end-to-end / NMS-free head. Output is <c>[B, N, 6]</c> where each row is
/// <c>x1, y1, x2, y2, score, classId</c> in input-size pixels, already sorted by score descending and
/// already de-duplicated by the model. Covers YOLOv10, YOLO26, and YOLOv8/v11 exported with
/// <c>nms=True</c>. Score filter only — no NMS.
///
/// Ported from SideGlance's own <c>Inference.Decoders.EndToEndDecoder</c>, adapted to map into
/// source-frame coordinates via <see cref="InferenceProfile.MapBoxToSource"/> (SideGlance never
/// scales/pads a submitted image, so it has no equivalent step).
/// </summary>
public sealed class EndToEndDecoder : IDetectionDecoder
{
    public List<ObjectDetection> Decode(IReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        IReadOnlyList<string> labels, DecodeThresholds thresholds, InferenceProfile profile, int batchSlot = 0)
    {
        var outTensor = outputs.First().AsTensor<float>();
        var dims = outTensor.Dimensions;
        if (dims.Length != 3 || dims[2] != 6)
            throw new DecoderShapeException(
                $"end-to-end decoder expects [B, N, 6], got [{string.Join(",", dims.ToArray())}].");

        ReadOnlySpan<float> output = outTensor is DenseTensor<float> dense ? dense.Buffer.Span : outTensor.ToArray();
        var rows = dims[1];
        var conf = thresholds.Confidence;
        var netW = profile.NetworkWidth;
        var netH = profile.NetworkHeight;
        var slotBase = batchSlot * rows * 6;

        var results = new List<ObjectDetection>();
        for (var i = 0; i < rows && results.Count < thresholds.MaxDetections; i++)
        {
            var p = slotBase + i * 6;
            var score = output[p + 4];
            if (score < conf) continue;

            var classId = (int)MathF.Round(output[p + 5]);
            if (classId < 0 || classId >= labels.Count) continue;

            var x0 = output[p] / netW;
            var y0 = output[p + 1] / netH;
            var x1 = output[p + 2] / netW;
            var y1 = output[p + 3] / netH;
            var cx = (x0 + x1) / 2.0;
            var cy = (y0 + y1) / 2.0;
            var w = x1 - x0;
            var h = y1 - y0;

            var (nx0, ny0, nx1, ny1) = profile.MapBoxToSource(cx, cy, w, h);
            var left = (int)Math.Round(Math.Clamp(nx0 * profile.SourceWidth, 0, profile.SourceWidth));
            var top = (int)Math.Round(Math.Clamp(ny0 * profile.SourceHeight, 0, profile.SourceHeight));
            var right = (int)Math.Round(Math.Clamp(nx1 * profile.SourceWidth, 0, profile.SourceWidth));
            var bottom = (int)Math.Round(Math.Clamp(ny1 * profile.SourceHeight, 0, profile.SourceHeight));
            if (right <= left || bottom <= top) continue;

            results.Add(new ObjectDetection
            {
                Label = new LabelModel { Index = classId, Name = labels[classId] },
                Confidence = score,
                BoundingBox = new SKRectI(left, top, right, bottom),
                Tail = [],
            });
        }

        return results;
    }
}
