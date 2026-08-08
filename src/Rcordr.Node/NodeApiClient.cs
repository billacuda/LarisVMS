using System.Net.Http.Json;
using Rcordr.Core.Dtos;

namespace Rcordr.Node;

/// <summary>REST client for Rcordr.Web's node control plane. Mirrors dploid's AgentApiClient
/// shape: BaseAddress = server URL, Bearer "{nodeId}:{secret}" once registered.</summary>
public class NodeApiClient
{
    private readonly HttpClient _http;

    public NodeApiClient(string serverUrl, bool acceptAnyCertificate)
    {
        var handler = new HttpClientHandler();
        if (acceptAnyCertificate)
        {
            // Same rationale as CameraService's "onvif" HttpClient: a self-hosted Rcordr.Web behind
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
}
