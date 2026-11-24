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

    public static readonly string LocalConfigPath = Path.Combine(LarisVMS.Core.LarisPaths.DataRoot, "proxy-endpoint.json");

    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    public static ProxyEndpointLocalConfig? ReadLocal()
    {
        try
        {
            if (!File.Exists(LocalConfigPath)) return null;
            return JsonSerializer.Deserialize<ProxyEndpointLocalConfig>(File.ReadAllText(LocalConfigPath), CaseInsensitive);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Overlays the <c>LARISVMS_CLIENT_*</c> environment variables on the local file, so a
    /// Docker container can be set up from its compose file alone. A variable that's set wins over the
    /// file; unset ones leave the file's value.</summary>
    internal static ProxyEndpointLocalConfig? WithEnvironment(ProxyEndpointLocalConfig? local, Func<string, string?> env)
    {
        var port = int.TryParse(env("LARISVMS_CLIENT_PORT"), out var p) && p is > 0 and <= 65535 ? p : (int?)null;
        var pfxPath = env("LARISVMS_CLIENT_PFX_PATH");
        var pfxPassword = env("LARISVMS_CLIENT_PFX_PASSWORD");
        var insecure = env("LARISVMS_CLIENT_ALLOW_INSECURE") is { Length: > 0 } i ? i is "1" or "true" or "True" : (bool?)null;
        var host = env("LARISVMS_CLIENT_ENDPOINT_HOST");
        if (port is null && string.IsNullOrEmpty(pfxPath) && string.IsNullOrEmpty(pfxPassword) && insecure is null && string.IsNullOrEmpty(host))
            return local;

        local ??= new ProxyEndpointLocalConfig();
        return local with
        {
            Port = port ?? local.Port,
            PfxPath = string.IsNullOrEmpty(pfxPath) ? local.PfxPath : pfxPath,
            PfxPassword = string.IsNullOrEmpty(pfxPassword) ? local.PfxPassword : pfxPassword,
            AllowInsecure = insecure ?? local.AllowInsecure,
            Host = string.IsNullOrEmpty(host) ? local.Host : host,
        };
    }

    public static ProxyEndpointConfig Resolve(ProxyConfigResponse? serverCached)
    {
        var local = WithEnvironment(ReadLocal(), Environment.GetEnvironmentVariable);

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
