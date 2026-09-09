using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LarisVMS.Core.Dtos;

namespace LarisVMS.Proxy;

/// <summary>Persisted registration for the relay. <see cref="CachedConfig"/> is the last
/// <see cref="ProxyConfigResponse"/> fetched — carried so a proxy that restarts while the central
/// server is unreachable can still serve from its last known node map. <see cref="CheckInNonce"/> is
/// the phase-5a rolling value, re-saved every heartbeat so a restart doesn't lose it.</summary>
public record ProxyConfig(string ServerUrl, Guid ProxyId, string Secret, string? CheckInNonce = null,
    ProxyConfigResponse? CachedConfig = null);

/// <summary>
/// %ProgramData%\LarisVMS\proxy.config — DPAPI-protected (LocalMachine), the same store shape as the
/// recorder node's node.config. Windows only.
/// </summary>
public static class ProxyConfigStore
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LarisVMS");
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "proxy.config");

    public static ProxyConfig? Load()
    {
        if (!File.Exists(ConfigPath)) return null;
        var protectedBytes = File.ReadAllBytes(ConfigPath);
        var json = OperatingSystem.IsWindows()
            ? Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.LocalMachine))
            : throw new PlatformNotSupportedException("Proxy config storage is only implemented for Windows.");
        return JsonSerializer.Deserialize<ProxyConfig>(json);
    }

    public static void Save(ProxyConfig config)
    {
        Directory.CreateDirectory(ConfigDir);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(config));
        var protectedBytes = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine)
            : throw new PlatformNotSupportedException("Proxy config storage is only implemented for Windows.");
        File.WriteAllBytes(ConfigPath, protectedBytes);
    }
}
