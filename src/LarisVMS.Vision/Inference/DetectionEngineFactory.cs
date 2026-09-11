using LarisVMS.Core.Enums;
using Microsoft.Extensions.Logging;

namespace LarisVMS.Vision.Inference;

/// <summary>Everything <see cref="HttpDetectionEngine"/> needs that isn't already a
/// <see cref="DetectionEngineFactory.Create"/> parameter — carried as one object so the external
/// backend doesn't smear an HttpClient and a source-resolution pair across the factory signature
/// for every caller that never uses them. Non-null only when
/// <c>Detection.Backend = "ExternalHttp"</c>.</summary>
public sealed record ExternalDetectionConfig(string BaseUrl, string Model, int SourceWidth, int SourceHeight, HttpClient Http,
    /// <summary>Sent as <c>Authorization: Bearer {ApiKey}</c> on every request — null/empty when the
    /// service needs no auth (e.g. a loopback-bound instance).</summary>
    string? ApiKey = null);

/// <summary>
/// Constructs the right <see cref="IDetectionEngine"/> for a resolved DetectionModelFamily —
/// CameraDetectionPipeline's replacement for its old direct <c>new YoloEngine(...)</c> call, now
/// that YoloEngine is gone (see IDetectionEngine's own doc comment for why YOLOv9/YoloDotNet was
/// dropped in favor of a first-party decoder per model family).
///
/// When <paramref name="external"/> is supplied the whole family switch is bypassed — the site
/// runs no local model at all, and <see cref="HttpDetectionEngine"/> handles both plain and sliced
/// requests against the configured service.
/// </summary>
public static class DetectionEngineFactory
{
    /// <param name="sliceLayout">Non-null only for <see cref="LarisVMS.Core.Enums.AspectMode.Slice"/>
    /// — threaded straight through to the engine's own constructor. See
    /// <see cref="ISlicedDetectionEngine"/>'s and DFineEngine/YoloXEngine/HttpDetectionEngine's own
    /// doc comments.</param>
    /// <param name="external">Non-null only for <c>Detection.Backend = "ExternalHttp"</c> — takes
    /// precedence over <paramref name="family"/>.</param>
    public static IDetectionEngine Create(DetectionModelFamily family, DFineWeights dfineWeights, YoloXSize yoloXSize,
        EngineOptions options, InferenceProfile profile, ILoggerFactory loggerFactory, SliceLayout? sliceLayout = null,
        ExternalDetectionConfig? external = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _ = yoloXSize; // the size selected the .onnx file already (ResolveModelPath); the engine only needs the path

        if (external is not null)
        {
            return new HttpDetectionEngine(external.BaseUrl, external.Model, profile, sliceLayout,
                external.SourceWidth, external.SourceHeight, external.Http, external.ApiKey, loggerFactory.CreateLogger<HttpDetectionEngine>());
        }

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
