using System.Text.Json;
using LarisVMS.Core.Dtos;

namespace LarisVMS.Proxy;

/// <summary>Optional local override file <c>%ProgramData%\LarisVMS\proxy-endpoint.json</c>, written
/// by <c>install-proxy.ps1</c>. Every field optional; a present file wins over what the server pushed.</summary>
public record ProxyEndpointLocalConfig(int Port = 0, string? PfxPath = null, string? PfxPassword = null,
    bool AllowInsecure = false, string? Host = null);

/// <summary>
/// Failover plan phase 2: the resolved settings for the proxy's HTTPS listener. Merges the local
/// <c>proxy-endpoint.json</c> over whatever the last server poll cached (local wins). The port is
/// read once at process start; the certificate hot-reloads via <c>CertWatcherService</c>.
/// </summary>
public sealed class ProxyEndpointConfig
{
    public const int DefaultPort = 4443;

    public int Port { get; init; }
    public string? PfxPath { get; init; }
    public string? PfxPassword { get; init; }
    public bool AllowInsecure { get; init; }
    public string Host { get; init; } = Environment.MachineName;

    public static readonly string LocalConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LarisVMS", "proxy-endpoint.json");

    public static ProxyEndpointLocalConfig? ReadLocal()
    {
        try
        {
            if (!File.Exists(LocalConfigPath)) return null;
            return JsonSerializer.Deserialize<ProxyEndpointLocalConfig>(File.ReadAllText(LocalConfigPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static ProxyEndpointConfig Resolve(ProxyConfigResponse? serverCached)
    {
        var local = ReadLocal();

        var port = local is { Port: > 0 } ? local.Port : DefaultPort;
        var pfxPath = !string.IsNullOrWhiteSpace(local?.PfxPath) ? local!.PfxPath : serverCached?.CertPfxPath;
        var pfxPassword = local?.PfxPassword is { Length: > 0 } ? local.PfxPassword : serverCached?.CertPfxPassword;
        var allowInsecure = local?.AllowInsecure == true || serverCached?.AllowInsecure == true;

        return new ProxyEndpointConfig
        {
            Port = port,
            PfxPath = string.IsNullOrWhiteSpace(pfxPath) ? null : pfxPath,
            PfxPassword = pfxPassword,
            AllowInsecure = allowInsecure,
            Host = string.IsNullOrWhiteSpace(local?.Host) ? Environment.MachineName : local!.Host!,
        };
    }
}
