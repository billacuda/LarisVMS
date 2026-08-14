using System.Security.Cryptography;
using System.Text.Json;
using LarisVMS.Core.Dtos;

namespace LarisVMS.Node;

/// <summary>CachedConfig is the last NodeConfigResponse this node successfully fetched — carried
/// along so a node that reboots while the central server is unreachable can resume recording from
/// it immediately instead of sitting idle until the server answers again (see NodeWorker's
/// reconcile loop). It's stale by definition; a live GetConfigAsync response always supersedes it
/// the moment one succeeds.</summary>
public record NodeConfig(string ServerUrl, Guid NodeId, string Secret, string MediaSigningKey,
    NodeConfigResponse? CachedConfig = null);

/// <summary>
/// Persists the node's registration (server URL, assigned NodeId, and secret) to
/// %ProgramData%\LarisVMS\node.config so a restart doesn't need to re-register. DPAPI-protected to
/// the local machine on Windows, matching dploid.Agent's config store. Linux support (AES-256-GCM
/// keyed off /etc/machine-id, per the plan) is not implemented yet — nodes are Windows-only for now.
/// Also carries the cached camera config (CachedConfig) — protected the same way since it includes
/// camera credentials, same sensitivity as the registration secret it travels with.
/// </summary>
public static class NodeConfigStore
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LarisVMS");
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "node.config");

    public static NodeConfig? Load()
    {
        if (!File.Exists(ConfigPath)) return null;
        var protectedBytes = File.ReadAllBytes(ConfigPath);
        var json = OperatingSystem.IsWindows()
            ? System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.LocalMachine))
            : throw new PlatformNotSupportedException("Node config storage is only implemented for Windows.");
        return JsonSerializer.Deserialize<NodeConfig>(json);
    }

    public static void Save(NodeConfig config)
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(config);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var protectedBytes = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine)
            : throw new PlatformNotSupportedException("Node config storage is only implemented for Windows.");
        File.WriteAllBytes(ConfigPath, protectedBytes);
    }
}
