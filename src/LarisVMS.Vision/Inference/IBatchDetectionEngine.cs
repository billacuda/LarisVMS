using YoloDotNet.Models;

namespace LarisVMS.Vision.Inference;

/// <summary>
/// Detection/hardware-acceleration overhaul, pass 3b: optional capability a detection engine may
/// additionally implement to run several same-sized images through one ONNX forward pass — used by
/// motion-guided native-scale re-detection to batch the whole-frame letterboxed pass together with
/// every native-scale tile in a single call, rather than one inference per image.
///
/// Deliberately not folded into <see cref="IDetectionEngine"/> itself: batching is a genuine D-FINE-
/// specific capability (its ONNX graph's dynamic batch dimension), not something every future model
/// family necessarily supports the same way. A caller checks for this via a type pattern (`_engine is
/// IBatchDetectionEngine`) and skips high-res re-detection entirely for an engine that doesn't
/// implement it, rather than forcing every <see cref="IDetectionEngine"/> to grow a method most
/// implementations couldn't use.
/// </summary>
public interface IBatchDetectionEngine
{
    /// <summary>Pass 4b: one packed-nv12 image per element, every image already exactly
    /// <see cref="InferenceProfile.NetworkWidth"/>x<see cref="InferenceProfile.NetworkHeight"/>
    /// (the whole-frame pass is pre-letterboxed by <see cref="Nv12Ops.LetterboxTo"/>, every tile is
    /// a native-scale 640x640 crop). Each image's own <see cref="InferenceProfile"/> only affects
    /// how *that* image's boxes are decoded back out of network space — a real letterbox profile for
    /// the whole-frame pass, an identity (Stretch) profile for a tile, whose result the caller
    /// re-anchors via <see cref="TileLayout.MapTileBoxToFrame"/>. Returns one detection list per
    /// image, in order — no cross-image NMS here (see <see cref="Nms"/>), that's the caller's job.</summary>
    List<List<ObjectDetection>> DetectBatch(IReadOnlyList<(byte[] Nv12, InferenceProfile Profile)> images, double confidence);
}
