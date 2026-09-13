using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
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
/// The request is the raw JPEG body with confidence/iou/slice carried as query parameters, not a
/// JSON envelope with the image base64-encoded inside it — the base64 form existed only because the
/// raw-bytes shape of the external endpoint had nowhere to carry those three fields. Removing the
/// base64 encode, the JSON serialize, and the two intermediate buffers they required was most of the
/// point: this is the same wire endpoint, just fewer copies to reach it. See
/// <see cref="ExternalDetectionMapper.FormatSliceQuery"/> for the slice query-value format.
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
    private static readonly MediaTypeHeaderValue JpegContentType = new("image/jpeg");
    private static readonly MediaTypeHeaderValue OctetStreamContentType = new("application/octet-stream");

    private readonly Uri _baseDetectUri;
    private readonly string? _sliceQuery;
    private readonly InferenceProfile _profile;
    private readonly SliceLayout? _sliceLayout;
    private readonly int _sourceWidth;
    private readonly int _sourceHeight;
    private readonly int _frameWidth;
    private readonly int _frameHeight;
    private readonly HttpClient _http;
    private readonly AuthenticationHeaderValue? _authHeader;
    private readonly ILogger _logger;
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };
    private readonly ExternalInferenceTransport _transport;
    // Precomputed once — layout/width/height never change for the life of this engine, same
    // reasoning as _sliceQuery. Empty (and unused) for Jpeg, which carries no such query fields.
    private readonly string _rawPixelQuery;

    public double? LastInferenceMilliseconds { get; private set; }
    /// <summary>JPEG-encode share of <see cref="LastInferenceMilliseconds"/> — see
    /// <see cref="LastTransportMilliseconds"/> for the rest of it. Added to attribute the gap
    /// between SideGlance's own reported <c>last_run_ms</c> (a few ms) and the much larger round
    /// trip this engine was already timing; "last value", not an average, same as
    /// <see cref="LastInferenceMilliseconds"/> itself.</summary>
    public double? LastEncodeMilliseconds { get; private set; }
    /// <summary>POST + response read/parse/map share of <see cref="LastInferenceMilliseconds"/> —
    /// everything after the JPEG is in hand. <c>LastTransportMilliseconds - </c> SideGlance's own
    /// <c>last_read_ms + last_decode_ms + last_pack_ms + last_run_ms + last_postprocess_ms</c> (from
    /// its <c>GET /v1/models</c>) is network + server queueing time neither side's own instrumentation
    /// otherwise names.</summary>
    public double? LastTransportMilliseconds { get; private set; }

    /// <param name="profile">The camera's own letterbox/stretch geometry for a non-sliced request.
    /// For a sliced request this is the degenerate per-slice identity profile (unused here — sliced
    /// boxes are mapped through <paramref name="sliceLayout"/> instead).</param>
    /// <param name="sliceLayout">Non-null only for <see cref="LarisVMS.Core.Enums.AspectMode.Slice"/>.
    /// Built with <c>networkSize</c> = the external model's own input size.</param>
    /// <param name="apiKey">Sent as <c>Authorization: Bearer {apiKey}</c> on every request — null or
    /// blank when the service needs no auth (e.g. SideGlance bound to loopback only).</param>
    public HttpDetectionEngine(string baseUrl, string model, InferenceProfile profile, SliceLayout? sliceLayout,
        int sourceWidth, int sourceHeight, HttpClient http, string? apiKey, ILogger logger,
        ExternalInferenceTransport transport = ExternalInferenceTransport.Jpeg)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(logger);

        _profile = profile;
        _sliceLayout = sliceLayout;
        // Precomputed once — the tile plan never changes for the life of this engine (a new one is
        // built whenever the pipeline restarts), so there is nothing to redo on every frame.
        _sliceQuery = sliceLayout is null ? null : ExternalDetectionMapper.FormatSliceQuery(ExternalDetectionMapper.BuildSliceSpec(sliceLayout));
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        _frameWidth = sliceLayout?.CaptureWidth ?? profile.NetworkWidth;
        _frameHeight = sliceLayout?.CaptureHeight ?? profile.NetworkHeight;
        _http = http;
        _authHeader = string.IsNullOrWhiteSpace(apiKey) ? null : new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        _logger = logger;
        _transport = transport;
        _rawPixelQuery = transport switch
        {
            ExternalInferenceTransport.PixelsYuv420 => $"&layout=pixels_yuv420sp&width={_frameWidth}&height={_frameHeight}",
            ExternalInferenceTransport.PixelsBgra => $"&layout=pixels_bgra32&width={_frameWidth}&height={_frameHeight}",
            _ => "",
        };

        // format=array so the response is the flat [{box,class,name,confidence}, ...] this engine
        // maps; model in the query string is the service's own model selector (GET /v1/models).
        // confidence_threshold/iou_threshold/slice are appended per-request in Detect (the first two
        // vary by call; slice is the same _sliceQuery every time).
        var root = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        _baseDetectUri = new Uri(root, $"v1/detect?model={Uri.EscapeDataString(model)}&format=array");

        _logger.LogInformation(
            "External HTTP detection engine ready: {Uri} ({Mode}, frame {W}x{H}, transport {Transport}).",
            _baseDetectUri, sliceLayout is null ? "whole-frame" : $"{sliceLayout.Slices.Count}-tile slice",
            _frameWidth, _frameHeight, _transport);
    }

    /// <summary><paramref name="frame"/> is the whole capture buffer at
    /// <see cref="InferenceProfile.NetworkWidth"/>×NetworkHeight (non-sliced) or
    /// <see cref="SliceLayout.CaptureWidth"/>×CaptureHeight (sliced) — BGRA8888 for
    /// <see cref="ExternalInferenceTransport.Jpeg"/>/<see cref="ExternalInferenceTransport.PixelsBgra"/>,
    /// nv12 for <see cref="ExternalInferenceTransport.PixelsYuv420"/> (the caller, not this engine,
    /// decides the buffer's actual format — via <c>CameraDetectionPipeline</c>'s own
    /// <c>gpuPreprocessing</c>, resolved from the same <see cref="ExternalInferenceTransport"/>).
    /// <paramref name="iou"/> is forwarded as the service's <c>iou_threshold</c>. Throws on a
    /// transport failure, a non-success status, or an unparseable body — the caller treats that as a
    /// skipped frame.</summary>
    public List<ObjectDetection> Detect(byte[] frame, double confidence, double iou)
    {
        var started = Stopwatch.GetTimestamp();

        // Only Jpeg pays an encode cost — the two raw-pixel transports send the capture buffer
        // exactly as the pipeline produced it, so there is nothing to do here but note the elapsed
        // time is (honestly) zero.
        byte[] body;
        MediaTypeHeaderValue contentType;
        if (_transport == ExternalInferenceTransport.Jpeg)
        {
            body = BgraOps.EncodeWholeFrameJpeg(frame, _frameWidth, _frameHeight, JpegQuality);
            contentType = JpegContentType;
        }
        else
        {
            body = frame;
            contentType = OctetStreamContentType;
        }
        var encodeElapsed = Stopwatch.GetElapsedTime(started);
        var transportStarted = Stopwatch.GetTimestamp();

        var uri = new StringBuilder(_baseDetectUri.ToString().Length + _rawPixelQuery.Length + 64)
            .Append(_baseDetectUri)
            .Append("&confidence_threshold=").Append(confidence.ToString(CultureInfo.InvariantCulture))
            .Append("&iou_threshold=").Append(iou.ToString(CultureInfo.InvariantCulture))
            .Append(_rawPixelQuery);
        if (_sliceQuery is not null)
            uri.Append("&slice=").Append(Uri.EscapeDataString(_sliceQuery));

        using var cts = new CancellationTokenSource(RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri.ToString())
        {
            // No base64, no JSON envelope, and (off Jpeg) no encode copy at all — `body` is either
            // EncodeWholeFrameJpeg's fresh output or the pipeline's own capture buffer, and
            // ByteArrayContent takes ownership by reference rather than copying it either way.
            Content = new ByteArrayContent(body) { Headers = { ContentType = contentType } },
        };
        if (_authHeader is not null) request.Headers.Authorization = _authHeader;

        using var response = _http.Send(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();

        using var stream = response.Content.ReadAsStream(cts.Token);
        var results = JsonSerializer.Deserialize<List<ExternalDetectionResult>>(stream, _json) ?? [];

        var mapped = _sliceLayout is { } layout
            ? ExternalDetectionMapper.MapSlicedResponse(results, layout, _sourceWidth, _sourceHeight)
            : ExternalDetectionMapper.MapPlainResponse(results, _profile);

        var transportElapsed = Stopwatch.GetElapsedTime(transportStarted);
        LastEncodeMilliseconds = encodeElapsed.TotalMilliseconds;
        LastTransportMilliseconds = transportElapsed.TotalMilliseconds;
        LastInferenceMilliseconds = (encodeElapsed + transportElapsed).TotalMilliseconds;
        return mapped;
    }

    public void Dispose() { /* the HttpClient is shared and owned by the DI factory, not this engine */ }
}
