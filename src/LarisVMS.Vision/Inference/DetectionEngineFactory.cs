using LarisVMS.Core.Enums;
using LarisVMS.Vision.Models;
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
    string? ApiKey = null,
    /// <summary>How the frame is sent — see <see cref="Inference.ExternalInferenceTransport"/>.
    /// Defaults to the always-safe <c>Jpeg</c> so a caller that doesn't care doesn't have to know this
    /// parameter exists.</summary>
    ExternalInferenceTransport Transport = ExternalInferenceTransport.Jpeg);

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
    /// <param name="customModel">Required (and only meaningful) when <paramref name="family"/> is
    /// <see cref="DetectionModelFamily.Custom"/> — the model <c>Detection.LocalModelName</c> resolved
    /// to via <see cref="Models.ModelDiscovery"/>. The caller (<c>CameraPipelineManager</c>) is
    /// responsible for the scan/lookup; this factory only constructs the engine from an already-
    /// resolved, already-validated result.</param>
    public static IDetectionEngine Create(DetectionModelFamily family, DFineWeights dfineWeights, YoloXSize yoloXSize,
        EngineOptions options, InferenceProfile profile, ILoggerFactory loggerFactory, SliceLayout? sliceLayout = null,
        ExternalDetectionConfig? external = null, DiscoveredModel? customModel = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _ = yoloXSize; // the size selected the .onnx file already (ResolveModelPath); the engine only needs the path

        if (external is not null)
        {
            return new HttpDetectionEngine(external.BaseUrl, external.Model, profile, sliceLayout,
                external.SourceWidth, external.SourceHeight, external.Http, external.ApiKey,
                loggerFactory.CreateLogger<HttpDetectionEngine>(), external.Transport);
        }

        return family switch
        {
            DetectionModelFamily.DFine => new DFineEngine(options, profile, DetectionModelCatalog.GetDFineLabels(dfineWeights),
                loggerFactory.CreateLogger<DFineEngine>(), sliceLayout),
            DetectionModelFamily.YoloX => new YoloXEngine(options, profile, DetectionModelCatalog.GetYoloXLabels(),
                loggerFactory.CreateLogger<YoloXEngine>(), sliceLayout),
            DetectionModelFamily.Custom => CreateCustom(options, profile, loggerFactory, sliceLayout, customModel),
            DetectionModelFamily.RfDetr => throw new NotSupportedException(
                "RF-DETR is not yet implemented — deferred scope, see the model-swap plan."),
            DetectionModelFamily.Auto => throw new ArgumentException(
                "ModelFamily must already be resolved to a concrete family by this point — " +
                "DetectionModelSelection.Choose runs node-side before this ever gets called.",
                nameof(family)),
            _ => throw new ArgumentOutOfRangeException(nameof(family), family, null),
        };
    }

    private static IDetectionEngine CreateCustom(EngineOptions options, InferenceProfile profile,
        ILoggerFactory loggerFactory, SliceLayout? sliceLayout, DiscoveredModel? customModel)
    {
        if (customModel is not { IsUsable: true, Descriptor: { } descriptor, Decoder: { } decoder })
            throw new InvalidOperationException(
                "DetectionModelFamily.Custom requires a resolved, usable DiscoveredModel (Detection.LocalModelName " +
                "must name a model whose metadata/sidecar resolved cleanly) — CameraPipelineManager must resolve " +
                "this via ModelDiscovery.Scan before calling DetectionEngineFactory.Create.");

        var modelsDirectory = Path.GetDirectoryName(customModel.OnnxPath)
            ?? throw new InvalidOperationException($"could not determine the containing directory of {customModel.OnnxPath}.");
        var labels = ResolveLabels(descriptor, modelsDirectory);
        return new GenericOnnxEngine(options, descriptor, decoder, labels, profile, loggerFactory.CreateLogger<GenericOnnxEngine>(), sliceLayout);
    }

    private static IReadOnlyList<string> ResolveLabels(ModelDescriptor descriptor, string modelsDirectory)
    {
        if (descriptor.Labels is { ValueKind: System.Text.Json.JsonValueKind.Array } arr)
            return arr.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();

        if (descriptor.Labels is { ValueKind: System.Text.Json.JsonValueKind.String } s
            && string.Equals(s.GetString(), "coco80", StringComparison.OrdinalIgnoreCase))
            return DFineLabels.YoloXCoco; // the bundled COCO-80 table — see DetectionModelCatalog's own doc comment.

        if (!string.IsNullOrWhiteSpace(descriptor.LabelsFile)
            && descriptor.TryResolveLabelsFile(modelsDirectory, out var resolved, out var error))
        {
            return File.ReadLines(resolved).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        }

        throw new InvalidOperationException("descriptor has no resolvable 'labels'/'labelsFile' — ModelDiscovery should have rejected this.");
    }
}
