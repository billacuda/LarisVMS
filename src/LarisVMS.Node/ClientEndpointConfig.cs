using System.Text.Json;
using LarisVMS.Core.Dtos;

namespace LarisVMS.Node;

/// <summary>Shape of the optional local override file
/// <c>%ProgramData%\LarisVMS\client-endpoint.json</c>, written by <c>install-node.ps1</c> (or by
/// hand for a test install). Every field optional; a present file wins over whatever the server
/// pushed.</summary>
public record ClientEndpointLocalConfig(bool Enabled = false, int Port = 0, string? PfxPath = null,
    string? PfxPassword = null, bool AllowInsecure = false, string? Host = null);

/// <summary>
/// Failover plan phase 1: the resolved settings for the node's second, client-facing Kestrel HTTPS
/// listener (a browser connecting straight to the node for live/playback). Merges the local
/// <c>client-endpoint.json</c> over whatever the last successful server poll cached in
/// <c>NodeConfig.CachedConfig</c> — local wins. Read once at process start: establishing or changing
/// the listener (enable, port) needs a node restart, the same "restart to apply" model
/// <c>SegmentSeconds</c> uses. The certificate itself hot-reloads without a restart — see
/// <see cref="CertWatcherService"/>.
/// </summary>
public sealed class ClientEndpointConfig
{
    public const int DefaultPort = 8555;

    public bool Enabled { get; init; }
    public int Port { get; init; }
    public string? PfxPath { get; init; }
    public string? PfxPassword { get; init; }
    public bool AllowInsecure { get; init; }
    /// <summary>Host name to put in the self-signed cert's CN/SAN — the FQDN a browser dials. From the
    /// local json's <c>Host</c>, else the machine name.</summary>
    public string Host { get; init; } = Environment.MachineName;

    public static readonly string LocalConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LarisVMS", "client-endpoint.json");

    public static ClientEndpointLocalConfig? ReadLocal()
    {
        try
        {
            if (!File.Exists(LocalConfigPath)) return null;
            return JsonSerializer.Deserialize<ClientEndpointLocalConfig>(File.ReadAllText(LocalConfigPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary><paramref name="serverCached"/> is <c>NodeConfig.CachedConfig</c> — null on a first
    /// run before any successful poll, in which case only the local json can enable the endpoint.</summary>
    public static ClientEndpointConfig Resolve(NodeConfigResponse? serverCached)
    {
        var local = ReadLocal();

        var enabled = local?.Enabled == true || serverCached?.ClientEndpointEnabled == true;
        var port = local is { Port: > 0 } ? local.Port
            : serverCached is { ClientEndpointPort: > 0 } ? serverCached.ClientEndpointPort
            : DefaultPort;
        var pfxPath = !string.IsNullOrWhiteSpace(local?.PfxPath) ? local!.PfxPath : serverCached?.ClientCertPfxPath;
        var pfxPassword = local?.PfxPassword is { Length: > 0 } ? local.PfxPassword : serverCached?.ClientCertPfxPassword;
        var allowInsecure = local?.AllowInsecure == true || serverCached?.ClientEndpointAllowInsecure == true;

        return new ClientEndpointConfig
        {
            Enabled = enabled,
            Port = port,
            PfxPath = string.IsNullOrWhiteSpace(pfxPath) ? null : pfxPath,
            PfxPassword = pfxPassword,
            AllowInsecure = allowInsecure,
            Host = string.IsNullOrWhiteSpace(local?.Host) ? Environment.MachineName : local!.Host!,
        };
    }
}
