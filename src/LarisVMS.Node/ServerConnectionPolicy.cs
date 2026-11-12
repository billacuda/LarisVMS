using System.Net;

namespace LarisVMS.Node;

/// <summary>
/// Encrypted connections from the server (Nodes → the node → Encrypt connections from the server,
/// opt-in). While it's on and this node's HTTPS endpoint is running, the server and media proxies
/// reach it there, so the plain LAN port stops serving media and control requests: someone on the
/// network can't read a token or video off it. <c>/health</c> stays open for failover probes, and
/// this machine's own Vision Service still reaches it on loopback.
/// </summary>
public static class ServerConnectionPolicy
{
    private static volatile bool _encryptedOnly;

    /// <summary>The server's latest setting for this node (NodeConfigResponse.EncryptServerConnection).</summary>
    public static bool EncryptedOnly
    {
        get => _encryptedOnly;
        set => _encryptedOnly = value;
    }

    public static bool ShouldRefuse(bool encryptedOnly, bool httpsRunning, int localPort, int livePort,
        IPAddress? remote, string? path)
    {
        if (!encryptedOnly || !httpsRunning || localPort != livePort) return false;
        if (remote is not null && IPAddress.IsLoopback(remote)) return false;
        return !string.Equals(path, "/health", StringComparison.OrdinalIgnoreCase);
    }
}
