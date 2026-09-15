using LarisVMS.Vision.Models;
using Microsoft.ML.OnnxRuntime;

namespace LarisVMS.Vision.Inference.Decoders;

/// <summary>Per-model decode settings, from the descriptor (with per-request overrides applied).
/// Ported from SideGlance's <c>Inference.Decoders.DecodeThresholds</c>.</summary>
public readonly record struct DecodeThresholds(
    float Confidence,
    float Iou,
    int MaxDetections,
    bool ClassAgnosticNms)
{
    public static DecodeThresholds From(ModelDescriptor d) => new(
        (float)d.ConfidenceThreshold, (float)d.IouThreshold, d.MaxDetections, d.ClassAgnosticNms);
}

/// <summary>
/// Turns a model's raw ONNX Runtime output into <see cref="YoloDotNet.Models.ObjectDetection"/>,
/// LarisVMS's common detection shape (so ByteTracker/MovementClassifier/CocoCategoryMap and every
/// downstream consumer stay unchanged regardless of which model family produced it — same role
/// <see cref="DFineDecoder"/>/<see cref="YoloXDecoder"/> already play for the two built-in engines).
/// NMS, if the head needs it, happens inside the decoder.
///
/// Adapted from SideGlance's own <c>Inference.Decoders.IDetectionDecoder</c>: that interface takes one
/// flattened output tensor, which fits YOLO-family single-output heads but not D-FINE's two named
/// outputs (<c>logits</c> + <c>pred_boxes</c>) — this version takes the whole named output collection
/// instead, and <see cref="InferenceProfile"/> to map network-space boxes back into source-frame
/// coordinates (SideGlance never needs this: it never scales/pads a submitted image).
///
/// <paramref name="batchSlot"/> (formerly dropped when this was single-frame-only, restored for
/// Slice-mode support — see <see cref="GenericOnnxEngine"/>'s own doc comment): which slot of the
/// output's own batch dimension to decode. Always 0 for a plain single-frame call; a Slice-mode
/// engine calls this once per slice with the same <paramref name="outputs"/> collection and an
/// increasing slot index, since <c>OnnxPreprocessHead.MergeSliced</c> produces one batched
/// <c>[N,...]</c> forward pass rather than N separate calls.
/// </summary>
public interface IDetectionDecoder
{
    List<YoloDotNet.Models.ObjectDetection> Decode(
        IReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        IReadOnlyList<string> labels,
        DecodeThresholds thresholds,
        InferenceProfile profile,
        int batchSlot = 0);
}
