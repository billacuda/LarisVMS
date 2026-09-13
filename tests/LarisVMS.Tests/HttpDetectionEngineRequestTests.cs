using System.Net;
using System.Text;
using LarisVMS.Core.Enums;
using LarisVMS.Vision.Inference;
using Microsoft.Extensions.Logging.Abstractions;

namespace LarisVMS.Tests;

/// <summary>
/// Covers the actual HTTP request <see cref="HttpDetectionEngine.Detect"/> sends — no prior test
/// exercised this at all. The change worth pinning: the request used to be a JSON body with the
/// image base64-encoded inside it; it is now the raw JPEG bytes as the body, with
/// confidence/iou/slice carried as query parameters instead (see the class's own doc comment for
/// why). A regression back to the old shape would still "work" functionally against a SideGlance new
/// enough to accept both, so only a test that inspects the actual request — not just that
/// <c>Detect</c> returns the right detections — would catch it.
/// </summary>
public class HttpDetectionEngineRequestTests
{
    /// <summary>Captures the single outgoing request and returns a canned <c>format=array</c> body.
    /// Overrides the synchronous <c>Send</c>, not <c>SendAsync</c> — <see cref="HttpDetectionEngine.Detect"/>
    /// calls <c>HttpClient.Send</c>, which requires the handler to support synchronous dispatch
    /// directly; an async-only override would throw <see cref="NotSupportedException"/>.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public byte[]? LastRequestBody { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastRequestBody = request.Content?.ReadAsByteArrayAsync(ct).GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json"),
            };
        }

        // HttpMessageHandler.SendAsync is abstract and must be implemented even though this test
        // suite only ever calls the synchronous Send path (HttpDetectionEngine.Detect uses
        // HttpClient.Send, not SendAsync) — never expected to be hit here.
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(Send(request, ct));
    }

    private static byte[] Bgra(int w, int h) => new byte[w * h * 4];

    [Fact]
    public void Detect_sends_the_raw_jpeg_body_with_no_json_envelope()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var profile = InferenceProfile.Create(64, 64, AspectMode.Stretch, 64);
        var engine = new HttpDetectionEngine("http://10.0.0.5:8642", "synthetic", profile, null,
            64, 64, http, apiKey: null, NullLogger.Instance);

        engine.Detect(Bgra(64, 64), confidence: 0.35, iou: 0.5);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("image/jpeg", handler.LastRequest!.Content!.Headers.ContentType!.MediaType);
        // A JPEG body starts with the SOI marker 0xFFD8 — proof this is the raw image, not a
        // '{"image":"<base64>",...}' JSON document (which would start with '{' = 0x7B).
        Assert.Equal(0xFF, handler.LastRequestBody![0]);
        Assert.Equal(0xD8, handler.LastRequestBody[1]);
        Assert.Null(handler.LastRequest.Headers.Authorization);
    }

    [Fact]
    public void Detect_carries_confidence_and_iou_as_query_parameters()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var profile = InferenceProfile.Create(64, 64, AspectMode.Stretch, 64);
        var engine = new HttpDetectionEngine("http://10.0.0.5:8642", "synthetic", profile, null,
            64, 64, http, apiKey: null, NullLogger.Instance);

        engine.Detect(Bgra(64, 64), confidence: 0.42, iou: 0.6);

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("model=synthetic", query);
        Assert.Contains("format=array", query);
        Assert.Contains("confidence_threshold=0.42", query);
        Assert.Contains("iou_threshold=0.6", query);
        Assert.DoesNotContain("slice=", query);
    }

    [Fact]
    public void Detect_carries_the_slice_plan_as_a_query_parameter_when_configured()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var layout = SliceLayout.Create(1280, 640, 640);
        var identityProfile = InferenceProfile.Create(640, 640, AspectMode.Stretch, 640);
        var engine = new HttpDetectionEngine("http://10.0.0.5:8642", "synthetic", identityProfile, layout,
            1280, 640, http, apiKey: null, NullLogger.Instance);

        engine.Detect(Bgra(layout.CaptureWidth, layout.CaptureHeight), confidence: 0.35, iou: 0.5);

        var query = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        var expectedTiles = string.Join(',', layout.Slices.Select(s => $"{s.X}x{s.Y}"));
        Assert.Contains($"slice={expectedTiles}", query);
        // full_width/full_height are deliberately not sent — see FormatSliceQuery's own doc comment.
        Assert.DoesNotContain("full_width", query);
    }

    /// <summary>Stage 0c: LastInferenceMilliseconds used to be the only timing this engine exposed —
    /// now it's the sum of a JPEG-encode share and a POST/parse/map "transport" share, each readable
    /// on its own so a slow round trip can be attributed to one side or the other (see
    /// <see cref="HttpDetectionEngine.LastEncodeMilliseconds"/>'s own doc comment).</summary>
    [Fact]
    public void Detect_splits_last_inference_into_encode_and_transport()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var profile = InferenceProfile.Create(64, 64, AspectMode.Stretch, 64);
        var engine = new HttpDetectionEngine("http://10.0.0.5:8642", "synthetic", profile, null,
            64, 64, http, apiKey: null, NullLogger.Instance);

        engine.Detect(Bgra(64, 64), confidence: 0.35, iou: 0.5);

        Assert.NotNull(engine.LastEncodeMilliseconds);
        Assert.NotNull(engine.LastTransportMilliseconds);
        Assert.True(engine.LastEncodeMilliseconds >= 0);
        Assert.True(engine.LastTransportMilliseconds >= 0);
        Assert.Equal(engine.LastInferenceMilliseconds!.Value,
            engine.LastEncodeMilliseconds!.Value + engine.LastTransportMilliseconds!.Value, precision: 6);
    }

    /// <summary>Stage 4: ExternalInferenceTransport.PixelsYuv420 sends the capture buffer raw — no
    /// JPEG encode, no envelope — with layout/width/height carried as query parameters exactly the
    /// way SideGlance's raw-pixel /v1/detect branch expects them.</summary>
    [Fact]
    public void Detect_with_pixels_yuv420_transport_sends_the_raw_nv12_body_with_layout_query_params()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var profile = InferenceProfile.Create(64, 64, AspectMode.Stretch, 64);
        var engine = new HttpDetectionEngine("http://10.0.0.5:8642", "synthetic", profile, null,
            64, 64, http, apiKey: null, NullLogger.Instance, ExternalInferenceTransport.PixelsYuv420);

        var nv12 = new byte[64 * 64 * 3 / 2];
        for (var i = 0; i < nv12.Length; i++) nv12[i] = (byte)i;
        engine.Detect(nv12, confidence: 0.35, iou: 0.5);

        Assert.Equal("application/octet-stream", handler.LastRequest!.Content!.Headers.ContentType!.MediaType);
        Assert.Equal(nv12, handler.LastRequestBody);
        var query = handler.LastRequest.RequestUri!.Query;
        Assert.Contains("layout=pixels_yuv420sp", query);
        Assert.Contains("width=64", query);
        Assert.Contains("height=64", query);
    }

    [Fact]
    public void Detect_with_pixels_bgra_transport_sends_the_raw_bgra_body_with_layout_query_params()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var profile = InferenceProfile.Create(64, 64, AspectMode.Stretch, 64);
        var engine = new HttpDetectionEngine("http://10.0.0.5:8642", "synthetic", profile, null,
            64, 64, http, apiKey: null, NullLogger.Instance, ExternalInferenceTransport.PixelsBgra);

        var bgra = Bgra(64, 64);
        engine.Detect(bgra, confidence: 0.35, iou: 0.5);

        Assert.Equal("application/octet-stream", handler.LastRequest!.Content!.Headers.ContentType!.MediaType);
        Assert.Equal(bgra, handler.LastRequestBody);
        var query = handler.LastRequest.RequestUri!.Query;
        Assert.Contains("layout=pixels_bgra32", query);
    }

    [Fact]
    public void Detect_default_transport_still_sends_jpeg()
    {
        // No transport argument — proves the default parameter really is Jpeg, not PixelsYuv420/Bgra,
        // so every existing caller (constructed before this parameter existed) keeps today's behaviour.
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var profile = InferenceProfile.Create(64, 64, AspectMode.Stretch, 64);
        var engine = new HttpDetectionEngine("http://10.0.0.5:8642", "synthetic", profile, null,
            64, 64, http, apiKey: null, NullLogger.Instance);

        engine.Detect(Bgra(64, 64), confidence: 0.35, iou: 0.5);

        Assert.Equal("image/jpeg", handler.LastRequest!.Content!.Headers.ContentType!.MediaType);
        Assert.DoesNotContain("layout=", handler.LastRequest.RequestUri!.Query);
    }

    [Fact]
    public void Detect_sends_a_bearer_token_when_an_api_key_is_configured()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var profile = InferenceProfile.Create(64, 64, AspectMode.Stretch, 64);
        var engine = new HttpDetectionEngine("http://10.0.0.5:8642", "synthetic", profile, null,
            64, 64, http, apiKey: "  secret-key  ", NullLogger.Instance);

        engine.Detect(Bgra(64, 64), confidence: 0.35, iou: 0.5);

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("secret-key", handler.LastRequest.Headers.Authorization.Parameter);
    }
}
