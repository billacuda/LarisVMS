using System.Net.Http.Json;
using LarisVMS.Core.Dtos;

namespace LarisVMS.Proxy;

/// <summary>REST client for LarisVMS.Web's proxy control plane (<c>/api/proxies/*</c>). Bearer
/// "{proxyId}:{secret}" once registered — the same shape as LarisVMS.Node's NodeApiClient.</summary>
public class ProxyApiClient
{
    private readonly HttpClient _http;

    public ProxyApiClient(string serverUrl, bool acceptAnyCertificate)
    {
        var handler = new HttpClientHandler();
        if (acceptAnyCertificate)
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    public void SetCredentials(Guid proxyId, string secret)
        => _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", $"{proxyId}:{secret}");

    public async Task<ProxyRegisterResponse> RegisterAsync(ProxyRegisterRequest request, CancellationToken ct)
    {
        var response = await _http.PostAsJsonAsync("api/proxies/register", request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProxyRegisterResponse>(ct))!;
    }

    public async Task<ProxyHeartbeatResponse> HeartbeatAsync(ProxyHeartbeatRequest request, CancellationToken ct)
    {
        var response = await _http.PostAsJsonAsync("api/proxies/heartbeat", request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProxyHeartbeatResponse>(ct))!;
    }

    public async Task<ProxyConfigResponse> GetConfigAsync(CancellationToken ct)
    {
        var response = await _http.GetAsync("api/proxies/config", ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProxyConfigResponse>(ct))!;
    }
}
