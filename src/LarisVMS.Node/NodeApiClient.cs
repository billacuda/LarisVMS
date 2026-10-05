using System.Net.Http.Json;
using LarisVMS.Core.Dtos;

namespace LarisVMS.Node;

/// <summary>REST client for LarisVMS.Web's node control plane. Mirrors dploid's AgentApiClient
/// shape: BaseAddress = server URL, Bearer "{nodeId}:{secret}" once registered.</summary>
public class NodeApiClient
{
    private readonly HttpClient _http;

    // A second client with a much longer timeout for the one large transfer the node pulls from the
    // server: a YOLOX detection model (yolox_x.onnx is ~100 MB+), fetched once per size and cached.
    private readonly HttpClient _downloadHttp;

    public NodeApiClient(string serverUrl, bool acceptAnyCertificate)
    {
        HttpClientHandler MakeHandler()
        {
            var h = new HttpClientHandler();
            if (acceptAnyCertificate)
            {
                // Same rationale as CameraService's "onvif" HttpClient: a self-hosted LarisVMS.Web
                // behind a self-signed cert on a LAN is a normal deployment shape, not a
                // misconfiguration, and there is no CA a home/small-business install would have.
                h.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
            }
            return h;
        }

        var baseAddress = new Uri(serverUrl.TrimEnd('/') + "/");
        _http = new HttpClient(MakeHandler()) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(15) };
        _downloadHttp = new HttpClient(MakeHandler()) { BaseAddress = baseAddress, Timeout = TimeSpan.FromMinutes(10) };
    }

    public void SetCredentials(Guid nodeId, string secret)
    {
        var auth = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", $"{nodeId}:{secret}");
        _http.DefaultRequestHeaders.Authorization = auth;
        _downloadHttp.DefaultRequestHeaders.Authorization = auth;
    }

    /// <summary>Streams a bundled/cached detection model file from the server (YOLOX models aren't
    /// shipped in the node package). The server serves it from its own detection-models cache,
    /// fetching once from the pinned upstream on a miss.</summary>
    public async Task<Stream> OpenDetectionModelStreamAsync(string family, string variant, CancellationToken ct)
    {
        var response = await _downloadHttp.GetAsync(
            $"api/nodes/detection-model/{Uri.EscapeDataString(family)}/{Uri.EscapeDataString(variant)}",
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(ct);
    }

    /// <summary>Size + SHA-256 of a large Vision Service native dependency the server has seeded
    /// (currently only "cuda-provider"), or null if it has none — the node then keeps running
    /// DirectML.</summary>
    public async Task<VisionNativeInfo?> GetVisionNativeInfoAsync(string name, CancellationToken ct)
    {
        var response = await _http.GetAsync($"api/nodes/vision-native/{Uri.EscapeDataString(name)}/info", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<VisionNativeInfo>(ct);
    }

    /// <summary>Streams a large Vision Service native dependency from the server — see
    /// <see cref="GetVisionNativeInfoAsync"/>. Uses the long-timeout client (the CUDA provider is
    /// ~320 MB).</summary>
    public async Task<Stream> OpenVisionNativeStreamAsync(string name, CancellationToken ct)
    {
        var response = await _downloadHttp.GetAsync(
            $"api/nodes/vision-native/{Uri.EscapeDataString(name)}",
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(ct);
    }

    public async Task<NodeRegisterResponse> RegisterAsync(NodeRegisterRequest request, CancellationToken ct)
    {
        var response = await _http.PostAsJsonAsync("api/nodes/register", request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<NodeRegisterResponse>(ct))!;
    }

    public async Task<NodeHeartbeatResponse> HeartbeatAsync(NodeHeartbeatRequest request, CancellationToken ct)
    {
        var response = await _http.PostAsJsonAsync("api/nodes/heartbeat", request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<NodeHeartbeatResponse>(ct))!;
    }

    public async Task<NodeConfigResponse> GetConfigAsync(CancellationToken ct)
    {
        var response = await _http.GetAsync("api/nodes/config", ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<NodeConfigResponse>(ct))!;
    }

    public async Task ReportSegmentsAsync(List<SegmentReportItem> segments, CancellationToken ct)
    {
        var response = await _http.PostAsJsonAsync("api/nodes/segments", segments, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteSegmentsAsync(List<string> filePaths, CancellationToken ct)
    {
        if (filePaths.Count == 0) return;
        var response = await _http.PostAsJsonAsync("api/nodes/segments/delete", new SegmentDeleteRequest(filePaths), ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task RelocateSegmentsAsync(List<SegmentRelocateItem> items, CancellationToken ct)
    {
        if (items.Count == 0) return;
        var response = await _http.PostAsJsonAsync("api/nodes/segments/relocate", new SegmentRelocateRequest(items), ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<List<string>> GetSegmentFilePathsAsync(CancellationToken ct)
    {
        var response = await _http.GetAsync("api/nodes/segments/paths", ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<string>>(ct))!;
    }

    public async Task<List<ExpiredSegmentDto>> GetExpiredSegmentsAsync(int limit, CancellationToken ct)
    {
        var response = await _http.GetAsync($"api/nodes/segments/expired?limit={limit}", ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<ExpiredSegmentDto>>(ct))!;
    }

    public async Task<List<string>> GetPrimaryTieredSegmentFilePathsAsync(CancellationToken ct)
    {
        var response = await _http.GetAsync("api/nodes/segments/primary-paths", ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<string>>(ct))!;
    }

    public async Task<List<long>> GetMotionSpanIdsAsync(CancellationToken ct)
    {
        var response = await _http.GetAsync("api/nodes/snapshots/span-ids", ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<long>>(ct))!;
    }

    public async Task ReportStreamInfoAsync(List<StreamInfoReportItem> items, CancellationToken ct)
    {
        if (items.Count == 0) return;
        var response = await _http.PostAsJsonAsync("api/nodes/streams/info", items, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task ReportMotionSpansAsync(List<MotionSpanReportItem> spans, CancellationToken ct)
    {
        if (spans.Count == 0) return;
        var response = await _http.PostAsJsonAsync("api/nodes/motion-spans", spans, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task ReportCameraEventsAsync(List<CameraEventReportItem> events, CancellationToken ct)
    {
        if (events.Count == 0) return;
        var response = await _http.PostAsJsonAsync("api/nodes/events", events, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task ReportExportCompleteAsync(List<ExportCompleteReportItem> items, CancellationToken ct)
    {
        if (items.Count == 0) return;
        var response = await _http.PostAsJsonAsync("api/nodes/exports/complete", items, ct);
        response.EnsureSuccessStatusCode();
    }
}
