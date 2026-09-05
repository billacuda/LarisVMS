using YoloDotNet.Models;

namespace LarisVMS.Vision.Inference;

/// <summary>
/// Optional capability a detection engine may additionally implement to run several same-sized
/// images through one ONNX forward pass instead of one inference call per image. Originally built
/// for motion-guided native-scale re-detection (removed — see git history); currently unused, kept
/// for the planned frame-slicing overhaul, which needs exactly this shape to batch a frame's slices
/// through one inference call.
///
/// Deliberately not folded into <see cref="IDetectionEngine"/> itself: batching is a genuine D-FINE-
/// specific capability today (its ONNX graph's dynamic batch dimension), not something every future
/// model family necessarily supports the same way. A caller checks for this via a type pattern
/// (`_engine is IBatchDetectionEngine`) rather than forcing every <see cref="IDetectionEngine"/> to
/// grow a method most implementations couldn't use.
/// </summary>
public interface IBatchDetectionEngine
{
    /// <summary>One packed-nv12 image per element, every image already exactly
    /// <see cref="InferenceProfile.NetworkWidth"/>x<see cref="InferenceProfile.NetworkHeight"/>.
    /// Each image's own <see cref="InferenceProfile"/> only affects how *that* image's boxes are
    /// decoded back out of network space — e.g. a real letterbox profile for a whole-frame pass, an
    /// identity (Stretch) profile for a plain crop. Returns one detection list per image, in order —
    /// no cross-image NMS here (see <see cref="Nms"/>), that's the caller's job.</summary>
    List<List<ObjectDetection>> DetectBatch(IReadOnlyList<(byte[] Nv12, InferenceProfile Profile)> images, double confidence);
}
