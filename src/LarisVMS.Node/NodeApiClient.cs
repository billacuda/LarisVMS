using System.Net.Http.Json;
using LarisVMS.Core.Dtos;

namespace LarisVMS.Node;

/// <summary>REST client for LarisVMS.Web's node control plane. Mirrors dploid's AgentApiClient
/// shape: BaseAddress = server URL, Bearer "{nodeId}:{secret}" once registered.</summary>
public class NodeApiClient
{
    private readonly HttpClient _http;

    public NodeApiClient(string serverUrl, bool acceptAnyCertificate)
    {
        var handler = new HttpClientHandler();
        if (acceptAnyCertificate)
        {
            // Same rationale as CameraService's "onvif" HttpClient: a self-hosted LarisVMS.Web behind
            // a self-signed cert on a LAN is a normal deployment shape, not a misconfiguration, and
            // there is no CA a home/small-business install would realistically have.
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }
        _http = new HttpClient(handler) { BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(15) };
    }

    public void SetCredentials(Guid nodeId, string secret)
        => _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", $"{nodeId}:{secret}");

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

    public async Task<List<string>> GetSegmentFilePathsAsync(CancellationToken ct)
    {
        var response = await _http.GetAsync("api/nodes/segments/paths", ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<string>>(ct))!;
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
