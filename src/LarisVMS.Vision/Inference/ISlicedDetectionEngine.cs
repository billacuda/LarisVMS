using YoloDotNet.Models;

namespace LarisVMS.Vision.Inference;

/// <summary>
/// Detection/hardware-acceleration overhaul, pass 4: optional capability for
/// <see cref="LarisVMS.Core.Enums.AspectMode.Slice"/>. Every implementation takes exactly **one**
/// nv12 buffer, the whole <c>SliceLayout.CaptureWidth</c>×<c>CaptureHeight</c> frame, never pre-cut
/// by the caller — and returns one detection list per slice, in <c>SliceLayout.Slices</c> order.
/// *How* the frame gets cut into slices is each implementation's own concern and differs by engine:
/// <see cref="DFineEngine"/>/<see cref="YoloXEngine"/> merge an accelerator-side head
/// (<see cref="OnnxPreprocessHead.MergeSliced"/>) that cuts and batches all N slices *inside* the
/// ONNX graph in one forward pass — verified safe only for those two specific, hand-checked exports,
/// so there is deliberately no CPU fallback for either of them (see their own constructors).
/// <see cref="GenericOnnxEngine"/> (an arbitrary user-supplied model) instead crops each tile out of
/// the nv12 buffer on the CPU and runs N separate batch-1 forward passes — slower, but correct
/// regardless of whether the model's own graph tolerates a batch dimension greater than 1, which
/// can't be safely assumed for a model this codebase has never seen before.
///
/// Deliberately a different shape from <see cref="IBatchDetectionEngine.DetectBatch"/>, not a reuse
/// of it: that method's whole contract is N *already separately-sized* images the caller hands in
/// one by one; here there is only ever one physical frame, and how it becomes N results is an
/// implementation detail the caller never manages.
/// </summary>
public interface ISlicedDetectionEngine
{
    /// <summary>Runs one forward pass over the whole captured frame and decodes each of the N
    /// resulting batch slots. <paramref name="iou"/> is not a parameter here for the same reason
    /// <see cref="IBatchDetectionEngine.DetectBatch"/> has none — merging duplicate detections
    /// across slices is <c>SliceMerge</c>'s job, applied by the caller after mapping every slice's
    /// own boxes into global coordinates, not something a single engine call can do on its own.</summary>
    List<List<ObjectDetection>> DetectSliced(byte[] wholeFrameNv12, double confidence);
}
