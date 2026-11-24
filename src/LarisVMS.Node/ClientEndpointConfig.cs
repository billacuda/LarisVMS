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
    /// <summary>The server asked for encrypted connections to this node: the endpoint runs for that
    /// even when browsers don't use it, and may use the self-signed certificate, which the server pins.</summary>
    public bool ServerTls { get; init; }
    /// <summary>Host name to put in the self-signed cert's CN/SAN — the FQDN a browser dials. From the
    /// local json's <c>Host</c>, else the machine name.</summary>
    public string Host { get; init; } = Environment.MachineName;
    /// <summary>The host set on this machine, reported to the server. Null when none is: the machine
    /// name <see cref="Host"/> falls back to isn't an address anyone chose for browsers.</summary>
    public string? ConfiguredHost { get; init; }

    public static readonly string LocalConfigPath = Path.Combine(LarisVMS.Core.LarisPaths.DataRoot, "client-endpoint.json");

    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    public static ClientEndpointLocalConfig? ReadLocal()
    {
        try
        {
            if (!File.Exists(LocalConfigPath)) return null;
            return JsonSerializer.Deserialize<ClientEndpointLocalConfig>(File.ReadAllText(LocalConfigPath), CaseInsensitive);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary><paramref name="serverCached"/> is <c>NodeConfig.CachedConfig</c> — null on a first
    /// run before any successful poll, in which case only the local json can enable the endpoint.</summary>
    /// <summary>Overlays the <c>LARISVMS_CLIENT_*</c> environment variables on the local file, so a
    /// Docker container can be set up from its compose file alone. A set port also turns the endpoint
    /// on, as <c>larisvms-node setup --client-port</c> does. Unset variables leave the file's values.</summary>
    internal static ClientEndpointLocalConfig? WithEnvironment(ClientEndpointLocalConfig? local, Func<string, string?> env)
    {
        var port = int.TryParse(env("LARISVMS_CLIENT_PORT"), out var p) && p is > 0 and <= 65535 ? p : (int?)null;
        var pfxPath = env("LARISVMS_CLIENT_PFX_PATH");
        var pfxPassword = env("LARISVMS_CLIENT_PFX_PASSWORD");
        var insecure = env("LARISVMS_CLIENT_ALLOW_INSECURE") is { Length: > 0 } i ? i is "1" or "true" or "True" : (bool?)null;
        var host = env("LARISVMS_CLIENT_ENDPOINT_HOST");
        if (port is null && string.IsNullOrEmpty(pfxPath) && string.IsNullOrEmpty(pfxPassword) && insecure is null && string.IsNullOrEmpty(host))
            return local;

        local ??= new ClientEndpointLocalConfig();
        return local with
        {
            Enabled = local.Enabled || port is not null,
            Port = port ?? local.Port,
            PfxPath = string.IsNullOrEmpty(pfxPath) ? local.PfxPath : pfxPath,
            PfxPassword = string.IsNullOrEmpty(pfxPassword) ? local.PfxPassword : pfxPassword,
            AllowInsecure = insecure ?? local.AllowInsecure,
            Host = string.IsNullOrEmpty(host) ? local.Host : host,
        };
    }

    public static ClientEndpointConfig Resolve(NodeConfigResponse? serverCached) =>
        Resolve(serverCached, WithEnvironment(ReadLocal(), Environment.GetEnvironmentVariable));

    internal static ClientEndpointConfig Resolve(NodeConfigResponse? serverCached, ClientEndpointLocalConfig? local)
    {
        var serverTls = serverCached?.EncryptServerConnection == true;
        var enabled = local?.Enabled == true || serverCached?.ClientEndpointEnabled == true || serverTls;
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
            ServerTls = serverTls,
            Host = string.IsNullOrWhiteSpace(local?.Host) ? Environment.MachineName : local!.Host!,
            ConfiguredHost = string.IsNullOrWhiteSpace(local?.Host) ? null : local!.Host!.Trim(),
        };
    }
}
