using SkiaSharp;
using YoloDotNet.Models;

namespace LarisVMS.Vision.Inference;

/// <summary>
/// One loaded detection model for one camera pipeline — the seam CameraDetectionPipeline actually
/// depends on, so it never needs to know whether the model underneath is YoloEngine (deleted, see
/// DetectionEngineFactory's own doc comment) or DFineEngine or a future RF-DETR/YOLOX engine.
/// Returns YoloDotNet's own <see cref="ObjectDetection"/> shape regardless of which model family
/// produced it, so ByteTracker/MovementClassifier/CocoCategoryMap and every downstream DTO stay
/// unchanged no matter which engine is behind this interface.
///
/// Not thread-safe — same one-instance-per-camera-pipeline ownership model YoloEngine already used.
/// </summary>
public interface IDetectionEngine : IDisposable
{
    /// <summary>Duration of the most recent <see cref="Detect"/> call, for diagnostics — null until
    /// the first call completes.</summary>
    double? LastInferenceMilliseconds { get; }

    /// <summary>
    /// Runs detection on one frame. <paramref name="iou"/> is meaningful only for a model family
    /// whose raw output needs non-maximum suppression (a dense per-pixel anchor grid, e.g. a future
    /// YOLOX engine) — inert on a DETR-style engine like <see cref="DFineEngine"/>, which never
    /// produces duplicate boxes to begin with. Implementations that ignore it must say so in their
    /// own doc comment rather than leaving it a silent no-op.
    /// </summary>
    List<ObjectDetection> Detect(SKBitmap frame, double confidence, double iou, SKRectI? roi = null);
}
