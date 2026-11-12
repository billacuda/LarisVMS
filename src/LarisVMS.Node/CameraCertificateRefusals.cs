using System.Collections.Concurrent;
using LarisVMS.Core.Dtos;

namespace LarisVMS.Node;

/// <summary>
/// Camera certificates this node refused under pinning (someone on the network may be posing as a
/// camera). Kept until the next heartbeat carries them to the server, which records them in the audit
/// log and tells Super Admins. At most one per camera address an hour, and a bounded number waiting.
/// </summary>
public static class CameraCertificateRefusals
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private const int MaxWaiting = 50;

    private static readonly ConcurrentQueue<NodeCameraCertificateRefusal> Waiting = new();
    private static readonly ConcurrentDictionary<string, DateTime> LastReported = new(StringComparer.OrdinalIgnoreCase);

    public static void Add(string host, string pinned, string presented)
    {
        var now = DateTime.UtcNow;
        if (LastReported.TryGetValue(host, out var last) && now - last < Interval) return;
        LastReported[host] = now;
        if (LastReported.Count > 1000) LastReported.Clear();
        if (Waiting.Count >= MaxWaiting) return;
        Waiting.Enqueue(new NodeCameraCertificateRefusal(host, presented, now));
    }

    /// <summary>Everything waiting, for this heartbeat; null when there's nothing.</summary>
    public static List<NodeCameraCertificateRefusal>? Drain()
    {
        var list = new List<NodeCameraCertificateRefusal>();
        while (Waiting.TryDequeue(out var r)) list.Add(r);
        return list.Count == 0 ? null : list;
    }

    /// <summary>A heartbeat that didn't reach the server: put them back for the next one.</summary>
    public static void Requeue(List<NodeCameraCertificateRefusal>? refusals)
    {
        foreach (var r in refusals ?? []) if (Waiting.Count < MaxWaiting) Waiting.Enqueue(r);
    }
}
