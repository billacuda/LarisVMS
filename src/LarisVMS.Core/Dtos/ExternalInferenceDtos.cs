using System.Text.Json.Serialization;

namespace LarisVMS.Core.Dtos;

// Wire DTOs for an OPTIONAL external HTTP inference service (Detection.Backend = "ExternalHttp") —
// an alternative to the in-process ONNX engine for sites that want models LarisVMS doesn't bundle.
// This is a THIRD-PARTY contract (SideGlance and anything that mimics it), not one of our own
// process-to-process protocols, so unlike VisionServiceDtos/NodeDtos every field here is annotated
// with its exact wire name and the (de)serializer is configured camelCase + case-insensitive next
// to each call site — there is no shared JsonSerializerOptions in this codebase to lean on.
//
// Shared via Core because two unrelated tiers consume it: LarisVMS.Vision's HttpDetectionEngine
// (the /v1/detect request + response) and LarisVMS.Web's ExternalInferenceProbe (the /healthz +
// /v1/models "test connection" button). Neither references the other.

/// <summary>Body of <c>POST {baseUrl}/v1/detect?model=NAME&amp;format=array</c>. The image is sent
/// base64 inside the JSON (rather than as raw <c>image/*</c> bytes) because the raw-bytes form of
/// the endpoint carries no way to pass <see cref="ConfidenceThreshold"/>/<see cref="IouThreshold"/>
/// or a <see cref="Slice"/> descriptor — and slicing is a first-class requirement here. The +33%
/// base64 overhead is immaterial on the LAN hop this always is.</summary>
public record ExternalDetectRequest(
    [property: JsonPropertyName("image")] string Image,
    [property: JsonPropertyName("confidence_threshold")] double ConfidenceThreshold,
    [property: JsonPropertyName("iou_threshold")] double IouThreshold,
    [property: JsonPropertyName("slice")] ExternalSliceSpec? Slice = null);

/// <summary>An explicit slice/tile plan for one submitted image — the exact geometry
/// <see cref="LarisVMS.Vision"/>'s <c>SliceLayout</c> already computes for the built-in Slice mode,
/// handed to the external service so it tiles + runs each tile + merges seam-straddling detections
/// itself and returns boxes in full-frame (submitted-image) pixel space. <see cref="FullWidth"/>/
/// <see cref="FullHeight"/> are the submitted image's own dimensions; the service applies the tiles
/// with no scaling, so the image's short side must already equal the model input size (it does —
/// the capture buffer is built at that size).</summary>
public record ExternalSliceSpec(
    [property: JsonPropertyName("full_width")] int FullWidth,
    [property: JsonPropertyName("full_height")] int FullHeight,
    [property: JsonPropertyName("tiles")] IReadOnlyList<ExternalSliceTile> Tiles);

public record ExternalSliceTile(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y);

/// <summary>One element of the <c>format=array</c> detection response:
/// <c>{ "box": {x1,y1,x2,y2}, "class": int, "name": str, "confidence": float }</c>. Box coordinates
/// are pixels in the submitted image's own space (input-size square for a plain request; full-frame
/// for a sliced one). <see cref="Name"/> is COCO-style so <c>CocoCategoryMap.Resolve</c> maps it to
/// a category with no extra table.</summary>
public record ExternalDetectionResult(
    [property: JsonPropertyName("box")] ExternalDetectionBox Box,
    [property: JsonPropertyName("class")] int Class,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("confidence")] double Confidence);

public record ExternalDetectionBox(
    [property: JsonPropertyName("x1")] double X1,
    [property: JsonPropertyName("y1")] double Y1,
    [property: JsonPropertyName("x2")] double X2,
    [property: JsonPropertyName("y2")] double Y2);

/// <summary><c>GET {baseUrl}/v1/models</c> — the picklist the "Test connection" button populates.
/// Only <see cref="Models"/> is required; the shape of each entry is
/// <see cref="ExternalModelInfo"/>.</summary>
public record ExternalModelsResponse(
    [property: JsonPropertyName("models")] IReadOnlyList<ExternalModelInfo> Models);

/// <summary>Per-model status from <c>/v1/models</c>. <see cref="InputSize"/> is the one field
/// LarisVMS must persist alongside the chosen model name — it sizes the ffmpeg capture buffer and
/// the <c>InferenceProfile</c>/<c>SliceLayout</c> geometry, and must be a positive multiple of 32
/// for the profile to accept it.</summary>
public record ExternalModelInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("decoder")] string? Decoder = null,
    [property: JsonPropertyName("inputSize")] int InputSize = 640,
    [property: JsonPropertyName("classCount")] int? ClassCount = null,
    [property: JsonPropertyName("batchMode")] string? BatchMode = null);

/// <summary><c>GET {baseUrl}/healthz</c> — surfaced verbatim by the "Test connection" result panel
/// so an operator can see the service is up, which build it is, and whether its models loaded.</summary>
public record ExternalHealthResponse(
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("version")] string? Version = null,
    [property: JsonPropertyName("backend")] string? Backend = null,
    [property: JsonPropertyName("models_ready")] IReadOnlyList<string>? ModelsReady = null,
    [property: JsonPropertyName("models_failed")] IReadOnlyList<string>? ModelsFailed = null);

/// <summary>Result of <c>ExternalInferenceProbe.ProbeAsync</c> — the "test connection + discover
/// models" call the Detection settings page makes. Never throws: <see cref="Error"/> is a short
/// human-readable reason on any failure (unreachable, non-200, unparseable body), the same
/// nullable-<c>Error</c> convention <c>CameraService.ProbeAsync</c>'s <c>CameraProbeSummary</c>
/// uses. On success <see cref="Error"/> is null and <see cref="Models"/> is the picklist.</summary>
public record ExternalInferenceProbeResult(
    string? Error,
    ExternalHealthResponse? Health = null,
    IReadOnlyList<ExternalModelInfo>? Models = null);
