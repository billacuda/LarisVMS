using System.Text;
using System.Text.Json;
using LarisVMS.Core.Dtos;

namespace LarisVMS.Proxy;

/// <summary>Persisted registration for the relay. <see cref="CachedConfig"/> is the last
/// <see cref="ProxyConfigResponse"/> fetched — carried so a proxy that restarts while the central
/// server is unreachable can still serve from its last known node map. <see cref="CheckInNonce"/> is
/// the phase-5a rolling value, re-saved every heartbeat so a restart doesn't lose it.</summary>
public record ProxyConfig(string ServerUrl, Guid ProxyId, string Secret, string? CheckInNonce = null,
    ProxyConfigResponse? CachedConfig = null, string? UpdateSigningKeyPem = null,
    string? ServerCertThumbprint = null, string? ServerCertificateCheck = null);

/// <summary>
/// proxy.config in the data folder (LarisPaths.DataRoot), encrypted to this machine with
/// MachineSecret — the same store shape as the recorder node's node.config.
/// </summary>
public static class ProxyConfigStore
{
    private static readonly string ConfigDir = LarisVMS.Core.LarisPaths.DataRoot;
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "proxy.config");

    /// <summary>Where the registration is kept (setup's --reregister removes it).</summary>
    internal static string FilePath => ConfigPath;

    public static ProxyConfig? Load()
    {
        if (!File.Exists(ConfigPath)) return null;
        var protectedBytes = File.ReadAllBytes(ConfigPath);
        var json = Encoding.UTF8.GetString(LarisVMS.Core.Security.MachineSecret.Unprotect(protectedBytes, null));
        return JsonSerializer.Deserialize<ProxyConfig>(json);
    }

    private static readonly object SaveGate = new();

    /// <summary>This process's live check of the server's certificate — see NodeConfigStore.Trust.</summary>
    public static LarisVMS.Core.Security.ServerCertificateTrust? Trust { get; private set; }

    public static LarisVMS.Core.Security.ServerCertificateTrust CreateTrust(ProxyConfig? config) =>
        Trust = new LarisVMS.Core.Security.ServerCertificateTrust(
            LarisVMS.Core.Security.ServerCertificateTrust.Parse(config?.ServerCertificateCheck), config?.ServerCertThumbprint, SaveTrust);

    private static void SaveTrust()
    {
        lock (SaveGate)
        {
            if (Load() is { } current) Save(current);
        }
    }

    public static void Save(ProxyConfig config)
    {
        lock (SaveGate)
        {
            if (Trust is { } trust)
                config = config with { ServerCertThumbprint = trust.PinnedThumbprint, ServerCertificateCheck = trust.Mode.ToString() };
            SaveCore(config);
        }
    }

    private static void SaveCore(ProxyConfig config)
    {
        Directory.CreateDirectory(ConfigDir);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(config));
        LarisVMS.Core.Security.MachineSecret.WriteFile(ConfigPath, LarisVMS.Core.Security.MachineSecret.Protect(bytes, null));
    }
}
