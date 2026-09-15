using Microsoft.ML.OnnxRuntime;
using YoloDotNet.Models;

namespace LarisVMS.Vision.Inference.Decoders;

/// <summary>
/// Wraps the existing, already-implemented <see cref="DFineDecoder"/> behind <see cref="IDetectionDecoder"/>
/// so a descriptor-driven model whose output names match D-FINE's two-output signature
/// (<c>logits</c>/<c>pred_boxes</c> — see <see cref="DecoderFactory"/>) goes through the exact same
/// decode math the built-in D-FINE engine uses, rather than a second bespoke DETR decoder. This is
/// what makes D-FINE-family models participate in the same one dispatch mechanism as the YOLO-family
/// decoders instead of a separate path.
/// </summary>
public sealed class DFineDecoderAdapter : IDetectionDecoder
{
    public List<ObjectDetection> Decode(IReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        IReadOnlyList<string> labels, DecodeThresholds thresholds, InferenceProfile profile, int batchSlot = 0)
    {
        var logitsTensor = outputs.First(o => o.Name == "logits").AsTensor<float>();
        var boxesTensor = outputs.First(o => o.Name == "pred_boxes").AsTensor<float>();

        var numQueries = logitsTensor.Dimensions[1];
        var numClasses = logitsTensor.Dimensions[2];

        // Same per-slot slicing DFineEngine.DetectBatch/DetectSliced already do at the engine level —
        // here it's behind the shared decoder interface instead, so GenericOnnxEngine's Slice-mode
        // path (one batched [N,...] forward pass) can decode each slot without its own copy of this.
        var logitsPerImage = numQueries * numClasses;
        var boxesPerImage = numQueries * 4;
        ReadOnlySpan<float> logits = logitsTensor is Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float> denseLogits
            ? denseLogits.Buffer.Span.Slice(batchSlot * logitsPerImage, logitsPerImage)
            : logitsTensor.ToArray().AsSpan(batchSlot * logitsPerImage, logitsPerImage);
        ReadOnlySpan<float> boxes = boxesTensor is Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float> denseBoxes
            ? denseBoxes.Buffer.Span.Slice(batchSlot * boxesPerImage, boxesPerImage)
            : boxesTensor.ToArray().AsSpan(batchSlot * boxesPerImage, boxesPerImage);

        return DFineDecoder.Decode(logits, boxes, numQueries, numClasses, labels, thresholds.Confidence, profile,
            thresholds.MaxDetections);
    }
}
