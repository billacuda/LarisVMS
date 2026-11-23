using LarisVMS.Core.Security;
using System.Text.Json;
using LarisVMS.Core.Dtos;

namespace LarisVMS.Node;

/// <summary>CachedConfig is the last NodeConfigResponse this node successfully fetched — carried
/// along so a node that reboots while the central server is unreachable can resume recording from
/// it immediately instead of sitting idle until the server answers again (see NodeWorker's
/// reconcile loop). It's stale by definition; a live GetConfigAsync response always supersedes it
/// the moment one succeeds.
///
/// CheckInNonce (failover plan phase 5a) is the single-use value the server last handed back
/// (NodeHeartbeatResponse.NextNonce); the node echoes it on its next heartbeat and persists each new
/// one here so a restart doesn't lose it and lock the node out. Null until the first heartbeat lands
/// (or after the server clears it). Secret is rotated in place (phase 5b) — the record is re-saved
/// with the new value the moment NewSecret arrives.</summary>
/// <param name="UpdateSigningKeyPem">The server's public signing key: only updates and native
/// libraries signed with it are installed (UpdateSignature).</param>
/// <param name="ServerCertThumbprint">The server certificate this node pinned (SHA-256), see ServerCertificateTrust.</param>
public record NodeConfig(string ServerUrl, Guid NodeId, string Secret, string MediaSigningKey,
    NodeConfigResponse? CachedConfig = null, string? CheckInNonce = null, string? UpdateSigningKeyPem = null,
    string? ServerCertThumbprint = null, string? ServerCertificateCheck = null);

/// <summary>
/// Persists the node's registration (server URL, assigned NodeId, and secret) to
/// node.config in the data folder (LarisPaths.DataRoot) so a restart doesn't need to re-register.
/// Encrypted to this machine with <see cref="MachineSecret"/>: DPAPI on Windows, AES-256-GCM keyed off
/// /etc/machine-id in an owner-only file on Linux.
/// Also carries the cached camera config (CachedConfig) — protected the same way since it includes
/// camera credentials, same sensitivity as the registration secret it travels with.
/// </summary>
public static class NodeConfigStore
{
    private static readonly string ConfigDir = LarisVMS.Core.LarisPaths.DataRoot;
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "node.config");

    /// <summary>Where the registration is kept (setup's --reregister removes it).</summary>
    internal static string FilePath => ConfigPath;

    public static NodeConfig? Load()
    {
        if (!File.Exists(ConfigPath)) return null;
        var protectedBytes = File.ReadAllBytes(ConfigPath);
        var json = System.Text.Encoding.UTF8.GetString(MachineSecret.Unprotect(protectedBytes, null));
        return JsonSerializer.Deserialize<NodeConfig>(json);
    }

    private static readonly object SaveGate = new();

    /// <summary>This process's live check of the server's certificate. Every save writes its current
    /// pin and mode, so a caller holding an older copy of the config can't undo them.</summary>
    public static ServerCertificateTrust? Trust { get; private set; }

    public static ServerCertificateTrust CreateTrust(NodeConfig? config) =>
        Trust = new ServerCertificateTrust(ServerCertificateTrust.Parse(config?.ServerCertificateCheck), config?.ServerCertThumbprint, SaveTrust);

    /// <summary>Writes a changed pin or mode into node.config (once the node is registered).</summary>
    private static void SaveTrust()
    {
        lock (SaveGate)
        {
            if (Load() is { } current) Save(current);
        }
    }

    public static void Save(NodeConfig config)
    {
        lock (SaveGate)
        {
            if (Trust is { } trust)
                config = config with { ServerCertThumbprint = trust.PinnedThumbprint, ServerCertificateCheck = trust.Mode.ToString() };
            SaveCore(config);
        }
    }

    private static void SaveCore(NodeConfig config)
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(config);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        MachineSecret.WriteFile(ConfigPath, MachineSecret.Protect(bytes, null));
    }
}
