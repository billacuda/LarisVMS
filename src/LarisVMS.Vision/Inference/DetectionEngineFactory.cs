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
    public static IDetectionEngine Create(DetectionModelFamily family, DFineWeights dfineWeights,
        EngineOptions options, InferenceProfile profile, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        return family switch
        {
            DetectionModelFamily.DFine => new DFineEngine(options, profile, DetectionModelCatalog.GetDFineLabels(dfineWeights),
                loggerFactory.CreateLogger<DFineEngine>()),
            DetectionModelFamily.RfDetr => throw new NotSupportedException(
                "RF-DETR is not yet implemented — deferred scope, see the model-swap plan."),
            DetectionModelFamily.YoloX => throw new NotSupportedException(
                "YOLOX is not yet implemented — deferred scope, see the model-swap plan."),
            DetectionModelFamily.Auto => throw new ArgumentException(
                "ModelFamily must already be resolved to a concrete family by this point — " +
                "DetectionModelSelection.Choose runs node-side before this ever gets called.",
                nameof(family)),
            _ => throw new ArgumentOutOfRangeException(nameof(family), family, null),
        };
    }
}
