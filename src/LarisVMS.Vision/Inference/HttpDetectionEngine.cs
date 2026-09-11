using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LarisVMS.Core.Dtos;
using Microsoft.Extensions.Logging;
using YoloDotNet.Models;

namespace LarisVMS.Vision.Inference;

/// <summary>
/// An <see cref="IDetectionEngine"/> that runs no model locally — it POSTs each decoded frame to a
/// user-configured external HTTP inference service (<c>Detection.Backend = "ExternalHttp"</c>) and
/// maps the JSON response back into the same <see cref="ObjectDetection"/> shape a local ONNX engine
/// produces. Everything past this seam (ByteTracker, MovementClassifier, per-label hysteresis, span
/// reporting, the live overlay) is identical to the built-in path — that is the whole reason the
/// backend choice lives behind <see cref="IDetectionEngine"/> rather than forking the pipeline.
///
/// Deliberately does NOT implement <see cref="ISlicedDetectionEngine"/>: when a <see cref="SliceLayout"/>
/// is supplied this engine sends the whole capture buffer plus an explicit tile plan and the
/// external service does the tiling + cross-seam merge, returning full-frame boxes — so
/// <see cref="Detect"/> already yields one merged list and <c>CameraDetectionPipeline</c>'s local
/// <c>MergeSliceDetections</c>/<c>SliceMerge</c> path is bypassed for this backend.
///
/// Not thread-safe, one instance per camera pipeline — same ownership model as every other engine,
/// even though this one holds no native session (the injected <see cref="HttpClient"/> is shared and
/// not disposed here).
/// </summary>
public sealed class HttpDetectionEngine : IDetectionEngine
{
    // Bounded per-call so a hung or slow external service degrades to skipped frames (the pipeline's
    // own inference try/catch counts them on the cadence line) rather than stalling the loop for
    // HttpClient's 100s default. The same "per-request CancelAfter below the client timeout" shape
    // DetectionOverlayHandler already uses for its poll.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private const int JpegQuality = 85;

    private readonly Uri _detectUri;
    private readonly InferenceProfile _profile;
    private readonly SliceLayout? _sliceLayout;
    private readonly ExternalSliceSpec? _sliceSpec;
    private readonly int _sourceWidth;
    private readonly int _sourceHeight;
    private readonly int _frameWidth;
    private readonly int _frameHeight;
    private readonly HttpClient _http;
    private readonly AuthenticationHeaderValue? _authHeader;
    private readonly ILogger _logger;
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public double? LastInferenceMilliseconds { get; private set; }

    /// <param name="profile">The camera's own letterbox/stretch geometry for a non-sliced request.
    /// For a sliced request this is the degenerate per-slice identity profile (unused here — sliced
    /// boxes are mapped through <paramref name="sliceLayout"/> instead).</param>
    /// <param name="sliceLayout">Non-null only for <see cref="LarisVMS.Core.Enums.AspectMode.Slice"/>.
    /// Built with <c>networkSize</c> = the external model's own input size.</param>
    /// <param name="apiKey">Sent as <c>Authorization: Bearer {apiKey}</c> on every request — null or
    /// blank when the service needs no auth (e.g. SideGlance bound to loopback only).</param>
    public HttpDetectionEngine(string baseUrl, string model, InferenceProfile profile, SliceLayout? sliceLayout,
        int sourceWidth, int sourceHeight, HttpClient http, string? apiKey, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(logger);

        _profile = profile;
        _sliceLayout = sliceLayout;
        _sliceSpec = sliceLayout is null ? null : ExternalDetectionMapper.BuildSliceSpec(sliceLayout);
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        _frameWidth = sliceLayout?.CaptureWidth ?? profile.NetworkWidth;
        _frameHeight = sliceLayout?.CaptureHeight ?? profile.NetworkHeight;
        _http = http;
        _authHeader = string.IsNullOrWhiteSpace(apiKey) ? null : new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        _logger = logger;

        // format=array so the response is the flat [{box,class,name,confidence}, ...] this engine
        // maps; model in the query string is the service's own model selector (GET /v1/models).
        var root = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        _detectUri = new Uri(root, $"v1/detect?model={Uri.EscapeDataString(model)}&format=array");

        _logger.LogInformation(
            "External HTTP detection engine ready: {Uri} ({Mode}, frame {W}x{H}).",
            _detectUri, sliceLayout is null ? "whole-frame" : $"{sliceLayout.Slices.Count}-tile slice", _frameWidth, _frameHeight);
    }

    /// <summary><paramref name="frame"/> is the whole BGRA8888 capture buffer at
    /// <see cref="InferenceProfile.NetworkWidth"/>×NetworkHeight (non-sliced) or
    /// <see cref="SliceLayout.CaptureWidth"/>×CaptureHeight (sliced) — this backend never uses nv12.
    /// <paramref name="iou"/> is forwarded as the service's <c>iou_threshold</c>. Throws on a
    /// transport failure, a non-success status, or an unparseable body — the caller treats that as a
    /// skipped frame.</summary>
    public List<ObjectDetection> Detect(byte[] frame, double confidence, double iou)
    {
        var sw = Stopwatch.StartNew();

        var jpeg = BgraOps.EncodeWholeFrameJpeg(frame, _frameWidth, _frameHeight, JpegQuality);
        var body = new ExternalDetectRequest(Convert.ToBase64String(jpeg), confidence, iou, _sliceSpec);

        using var cts = new CancellationTokenSource(RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, _detectUri)
        {
            Content = JsonContent.Create(body, options: _json),
        };
        if (_authHeader is not null) request.Headers.Authorization = _authHeader;

        using var response = _http.Send(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();

        using var stream = response.Content.ReadAsStream(cts.Token);
        var results = JsonSerializer.Deserialize<List<ExternalDetectionResult>>(stream, _json) ?? [];

        var mapped = _sliceLayout is { } layout
            ? ExternalDetectionMapper.MapSlicedResponse(results, layout, _sourceWidth, _sourceHeight)
            : ExternalDetectionMapper.MapPlainResponse(results, _profile);

        sw.Stop();
        LastInferenceMilliseconds = sw.Elapsed.TotalMilliseconds;
        return mapped;
    }

    public void Dispose() { /* the HttpClient is shared and owned by the DI factory, not this engine */ }
}
