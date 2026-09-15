using System.Net.Http.Json;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Security;

namespace LarisVMS.Web.Services;

/// <summary>
/// Shared "issue a signed node-control token, call the node's own inbound port" client — factored out
/// of <c>Admin/Nodes.cshtml.cs</c>'s <c>OnPostRestartAsync</c> (the original, POST-only version of
/// this exact pattern) so the model-discovery dropdown (a GET) can reuse it from both
/// <c>Admin/Nodes.cshtml.cs</c> (per-node) and <c>Admin/Settings/Detection.cshtml.cs</c> (fanned out
/// across every online node for the global default).
/// </summary>
public sealed class NodeControlClient(IHttpClientFactory httpClientFactory, ILogger<NodeControlClient> logger)
{
    /// <summary>Proxies a node's <c>GET /vision/models</c> — the models
    /// <c>LarisVMS.Vision.Models.ModelDiscovery</c> found in its own
    /// <c>C:\ProgramData\LarisVMS\models</c>. Never throws: a node with no reported address/signing
    /// key, or one that's simply unreachable (offline, AI detection not installed), reads the same as
    /// "no models available" rather than an error — the caller's UI stays usable either way.</summary>
    public async Task<IReadOnlyList<DiscoveredModelDto>> GetVisionModelsAsync(Node node, CancellationToken ct = default)
    {
        if (node is not { LastIpAddress: { } ip, LivePort: { } port, MediaSigningKey: { } key })
            return [];

        var token = MediaToken.IssueForNodeControl("vision-models", key, TimeSpan.FromSeconds(30));
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        try
        {
            var response = await client.SendAsync(MediaTokenRequest.Create(HttpMethod.Get,
                $"http://{ip}:{port}/vision/models?token={Uri.EscapeDataString(token)}", token), ct);
            if (!response.IsSuccessStatusCode) return [];

            return await response.Content.ReadFromJsonAsync<List<DiscoveredModelDto>>(cancellationToken: ct) ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogInformation(ex, "Could not fetch /vision/models from node {Node} ({Ip}:{Port}).", node.Name, ip, port);
            return [];
        }
    }

    /// <summary>Proxies a node's <c>POST /reconcile-now</c> — wakes its reconcile loop immediately
    /// instead of leaving it to notice a just-saved setting within its own 30s poll. Fire-and-forget
    /// by design from the caller's side: never throws, and a node that's briefly unreachable just means
    /// the 30s poll remains the correctness backstop, so callers should not block a Save on this.
    /// Returns whether the call reached the node and it accepted the request, purely for logging —
    /// callers are not expected to act differently either way.</summary>
    public async Task<bool> TriggerReconcileAsync(Node node, CancellationToken ct = default)
    {
        if (node is not { LastIpAddress: { } ip, LivePort: { } port, MediaSigningKey: { } key })
            return false;

        var token = MediaToken.IssueForNodeControl("reconcile-now", key, TimeSpan.FromSeconds(30));
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        try
        {
            var response = await client.SendAsync(MediaTokenRequest.Create(HttpMethod.Post,
                $"http://{ip}:{port}/reconcile-now?token={Uri.EscapeDataString(token)}", token), ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogInformation(ex, "Could not trigger /reconcile-now on node {Node} ({Ip}:{Port}) — it will still pick up the change within its own 30s poll.", node.Name, ip, port);
            return false;
        }
    }
}
