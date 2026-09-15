namespace LarisVMS.Core.Dtos;

/// <summary>One model <c>LarisVMS.Vision.Models.ModelDiscovery</c> found in a node's
/// <c>C:\ProgramData\LarisVMS\models</c> directory — the wire shape for Vision Service's
/// <c>GET /models</c>, Node's <c>GET /vision/models</c> proxy, and what
/// <c>Admin/Nodes.cshtml</c>/<c>Admin/Settings/Detection.cshtml</c> render as dropdown options.</summary>
/// <param name="Name">The model's name — the file's own base name unless a sidecar overrides it.
/// What an operator sets <c>Detection.LocalModelName</c> to.</param>
/// <param name="Decoder">The resolved <see cref="LarisVMS.Vision.Models.DecoderKind"/> name (e.g.
/// "Ultralytics", "DFine"), or null when unresolved.</param>
/// <param name="InputSize">The model's square input edge, or null when unresolved.</param>
/// <param name="Source">"OnnxMetadata", "JsonSidecar", or "Unresolved" — see
/// <see cref="LarisVMS.Vision.Models.ModelMetadataSource"/>.</param>
/// <param name="Warnings">Plain-language problems — e.g. "metadata not found — supply a
/// {name}.json sidecar." Non-empty only when <paramref name="Source"/> is "Unresolved" (or a
/// same-named metadata/sidecar disagreement was logged). Never hidden from the UI — a model with
/// warnings is still listed, just flagged.</param>
public record DiscoveredModelDto(string Name, string? Decoder, int? InputSize, string Source, IReadOnlyList<string> Warnings);

/// <summary>One watched camera's engine-build state — the wire shape for Vision Service's
/// <c>GET /cameras/status</c>, which <c>NodeWorker</c> polls and folds into
/// <see cref="StreamInfoReportItem"/> so the Dashboard can show a "still starting AI detection"
/// spinner instead of the camera silently reporting no detections. Kept separate from the existing
/// <c>GET /cameras</c> (a bare camera-id list <c>NodeWorker.PruneStaleVisionWatchesAsync</c> depends
/// on in that exact shape) rather than changing that endpoint's response shape.</summary>
public record VisionCameraStatusDto(Guid CameraId, bool IsEngineBuilding, bool EngineBuildFailed);
