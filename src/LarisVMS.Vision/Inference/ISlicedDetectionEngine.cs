using YoloDotNet.Models;

namespace LarisVMS.Vision.Inference;

/// <summary>
/// Detection/hardware-acceleration overhaul, pass 4: optional capability for
/// <see cref="LarisVMS.Core.Enums.AspectMode.Slice"/>'s GPU-native path. The engine's own
/// accelerator-side head (<see cref="OnnxPreprocessHead.MergeSliced"/>) already cuts one whole,
/// non-square captured frame into N overlapping <c>networkSize</c>-square slices and batches them
/// *inside* the ONNX graph — so <see cref="DetectSliced"/> takes exactly **one** nv12 buffer, the
/// whole <c>SliceLayout.CaptureWidth</c>×<c>CaptureHeight</c> frame, never pre-cut by the caller —
/// and returns one detection list per slice, in <c>SliceLayout.Slices</c> order.
///
/// Deliberately a different shape from <see cref="IBatchDetectionEngine.DetectBatch"/>, not a reuse
/// of it: that method's whole contract is N *already separately-sized* images the caller hands in
/// one by one; here there is only ever one physical frame, and the batching is an internal graph
/// detail the caller never sees or manages. Only implemented when the engine was built with a
/// <c>SliceLayout</c> and GPU preprocessing on — Slice mode requires GPU preprocessing (see
/// <see cref="CameraDetectionPipeline"/>'s own doc comment) precisely so this path exists instead of
/// a CPU fallback that would defeat the point of doing the slice on the accelerator at all.
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
