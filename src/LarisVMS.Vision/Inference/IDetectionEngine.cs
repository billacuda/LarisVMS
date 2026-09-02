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
    /// Runs detection on one raw frame buffer already at the engine's network input size. The
    /// buffer is either BGRA8888 (<c>W*H*4</c> bytes) or, when the engine was built with
    /// <see cref="EngineOptions.GpuPreprocessing"/>, packed nv12 (<c>W*H*3/2</c> bytes: H rows of Y
    /// then H/2 rows of interleaved UV) — the implementation knows which from its own construction.
    /// <paramref name="iou"/> is meaningful only for a model family whose raw output needs
    /// non-maximum suppression (a dense per-pixel anchor grid, e.g. a future YOLOX engine) — inert on
    /// a DETR-style engine like <see cref="DFineEngine"/>. Implementations that ignore it must say so.
    /// </summary>
    List<ObjectDetection> Detect(byte[] frame, double confidence, double iou);
}
