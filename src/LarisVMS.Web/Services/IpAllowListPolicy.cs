using System.Net;

namespace LarisVMS.Web.Services;

/// <summary>
/// Pure parsing/matching logic behind the M20 IP allow list settings (Security.ManagementIpAllowList /
/// Security.LiveViewIpAllowList) — extracted so it's unit-testable without a real request pipeline,
/// same shape as SessionLifetimePolicy.
///
/// An empty list means unrestricted ("open by default", per the roadmap ask) — same philosophy
/// PortSegmentationMiddleware's own unset-port default and CameraAccess's zero-rows default already
/// establish in this app: a restriction only ever narrows once an admin deliberately adds one.
/// </summary>
public static class IpAllowListPolicy
{
    public record ParseResult(IReadOnlyList<IPNetwork> Networks, IReadOnlyList<string> InvalidLines);

    /// <summary>Each line is blank/"#"-comment (ignored), a bare IP (treated as a full-host network —
    /// /32 for IPv4, /128 for IPv6), or a CIDR block ("10.0.0.0/24"). A line matching none of those is
    /// collected in InvalidLines rather than silently dropped, so a save with a typo fails loudly
    /// instead of quietly admitting fewer addresses than the admin intended.</summary>
    public static ParseResult Parse(string? raw)
    {
        var networks = new List<IPNetwork>();
        var invalid = new List<string>();

        foreach (var rawLine in (raw ?? string.Empty).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (line.Contains('/'))
            {
                if (IPNetwork.TryParse(line, out var network)) networks.Add(network);
                else invalid.Add(line);
            }
            else if (IPAddress.TryParse(line, out var address))
            {
                var prefixLength = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
                networks.Add(new IPNetwork(address, prefixLength));
            }
            else
            {
                invalid.Add(line);
            }
        }

        return new ParseResult(networks, invalid);
    }

    /// <summary>True if networks is empty (open by default) or remoteIp falls inside at least one of
    /// them. remoteIp is unmapped from its IPv4-mapped-IPv6 form first — a dual-stack Kestrel socket
    /// can report an IPv4 client as "::ffff:10.0.0.5", which would otherwise never match a plain
    /// "10.0.0.0/24" entry an admin actually meant to cover it.</summary>
    public static bool IsAllowed(IPAddress? remoteIp, IReadOnlyList<IPNetwork> networks)
    {
        if (networks.Count == 0) return true;
        if (remoteIp is null) return false;

        var candidate = remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4() : remoteIp;
        // Same-family check first: IPNetwork.Contains isn't guaranteed safe across mismatched address
        // families, and an IPv6-only entry should just never match an IPv4 client (and vice versa)
        // rather than risk an exception taking the whole request pipeline down.
        return networks.Any(n => n.BaseAddress.AddressFamily == candidate.AddressFamily && n.Contains(candidate));
    }

    /// <summary>Unmaps an IPv4-mapped-IPv6 address ("::ffff:10.0.0.5") back to plain IPv4 before it's
    /// stored or used to build a URL — same reasoning as <see cref="IsAllowed"/> above, but for every
    /// other place a Node/MediaProxy's remote IP gets captured (NodeAuthMiddleware, ProxyAuthMiddleware).
    /// A dual-stack Kestrel <c>ListenAnyIP</c> socket reports every IPv4 client this way; IIS never did,
    /// so this normalization was never needed before self-hosted Kestrel. Every downstream consumer
    /// (live/playback/snapshot proxying, node control calls, failover probing) builds a plain
    /// "http://{ip}:{port}/..." URL straight from the stored value — an unmapped "::ffff:x.x.x.x"
    /// there is simply not a valid host in that string, breaking the connection outright.</summary>
    public static string? Unmap(IPAddress? remoteIp)
    {
        if (remoteIp is null) return null;
        return (remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4() : remoteIp).ToString();
    }
}
