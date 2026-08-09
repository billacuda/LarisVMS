using System.Security.Cryptography;
using System.Text.Json;

namespace NidusVMS.Node;

public record NodeConfig(string ServerUrl, Guid NodeId, string Secret, string MediaSigningKey);

/// <summary>
/// Persists the node's registration (server URL, assigned NodeId, and secret) to
/// %ProgramData%\NidusVMS\node.config so a restart doesn't need to re-register. DPAPI-protected to
/// the local machine on Windows, matching dploid.Agent's config store. Linux support (AES-256-GCM
/// keyed off /etc/machine-id, per the plan) is not implemented yet — nodes are Windows-only for now.
/// </summary>
public static class NodeConfigStore
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NidusVMS");
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
