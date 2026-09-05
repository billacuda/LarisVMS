using LarisVMS.Core.Enums;
using Microsoft.Extensions.Logging;

namespace LarisVMS.Vision.Inference;

/// <summary>
/// Constructs the right <see cref="IDetectionEngine"/> for a resolved DetectionModelFamily —
/// CameraDetectionPipeline's replacement for its old direct <c>new YoloEngine(...)</c> call, now
/// that YoloEngine is gone (see IDetectionEngine's own doc comment for why YOLOv9/YoloDotNet was
/// dropped in favor of a first-party decoder per model family).
/// </summary>
public static class DetectionEngineFactory
{
    /// <param name="sliceLayout">Non-null only for <see cref="LarisVMS.Core.Enums.AspectMode.Slice"/>
    /// — threaded straight through to the engine's own constructor. See
    /// <see cref="ISlicedDetectionEngine"/>'s and DFineEngine/YoloXEngine's own doc comments.</param>
    public static IDetectionEngine Create(DetectionModelFamily family, DFineWeights dfineWeights, YoloXSize yoloXSize,
        EngineOptions options, InferenceProfile profile, ILoggerFactory loggerFactory, SliceLayout? sliceLayout = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _ = yoloXSize; // the size selected the .onnx file already (ResolveModelPath); the engine only needs the path

        return family switch
        {
            DetectionModelFamily.DFine => new DFineEngine(options, profile, DetectionModelCatalog.GetDFineLabels(dfineWeights),
                loggerFactory.CreateLogger<DFineEngine>(), sliceLayout),
            DetectionModelFamily.YoloX => new YoloXEngine(options, profile, DetectionModelCatalog.GetYoloXLabels(),
                loggerFactory.CreateLogger<YoloXEngine>(), sliceLayout),
            DetectionModelFamily.RfDetr => throw new NotSupportedException(
                "RF-DETR is not yet implemented — deferred scope, see the model-swap plan."),
            DetectionModelFamily.Auto => throw new ArgumentException(
                "ModelFamily must already be resolved to a concrete family by this point — " +
                "DetectionModelSelection.Choose runs node-side before this ever gets called.",
                nameof(family)),
            _ => throw new ArgumentOutOfRangeException(nameof(family), family, null),
        };
    }
}
