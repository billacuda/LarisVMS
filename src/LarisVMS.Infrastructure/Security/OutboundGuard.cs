using System.Net;
using System.Net.Sockets;

namespace LarisVMS.Infrastructure.Security;

/// <summary>
/// For outbound requests to addresses a user supplied (a browser's Web Push endpoint), so the server
/// can't be pointed at its own network: only the browser makers' push services are accepted, and the
/// connection itself refuses loopback, private, link-local, and other non-public addresses after DNS
/// resolution (so a public name that resolves to 10.x is still refused).
/// </summary>
public static class OutboundGuard
{
    // Chrome/Edge (FCM), Edge on Windows (WNS), Firefox (Mozilla autopush), Safari (Apple).
    private static readonly string[] PushHostSuffixes =
        ["fcm.googleapis.com", ".notify.windows.com", ".push.services.mozilla.com", ".push.apple.com"];

    public static bool IsKnownPushService(Uri endpoint) =>
        endpoint.Scheme == Uri.UriSchemeHttps
        && endpoint.IsDefaultPort
        && PushHostSuffixes.Any(s => s.StartsWith('.')
            ? endpoint.Host.EndsWith(s, StringComparison.OrdinalIgnoreCase)
            : endpoint.Host.Equals(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>A handler whose connections only reach public addresses.</summary>
    public static SocketsHttpHandler PublicOnlyHandler() => GuardedHandler(IsPublic, "isn't a public address");

    /// <summary>For service URLs an admin types (alert webhooks, ntfy, Slack, Teams): the LAN is fine,
    /// since people self-host these, but not this machine itself or link-local addresses, where cloud
    /// metadata services answer.</summary>
    public static SocketsHttpHandler NoLocalHandler() =>
        GuardedHandler(a => !IsHostOrLinkLocal(a, allowLoopback: false), "is this machine or a link-local address");

    /// <summary>Like <see cref="NoLocalHandler"/>, but this machine is allowed: an inference service
    /// is often bound to loopback next to the server.</summary>
    public static SocketsHttpHandler NoLinkLocalHandler() =>
        GuardedHandler(a => !IsHostOrLinkLocal(a, allowLoopback: true), "is a link-local address");

    /// <summary>For cameras (ONVIF): anything on the network, link-local too, since a camera with no
    /// DHCP lease falls back to 169.254.x.x. Only cloud metadata services, and addresses no camera
    /// can have, are refused. Accepts the self-signed certificate every HTTPS camera has, unless
    /// camera certificate pinning is on (CameraCertificatePins), when a camera's certificate must
    /// match the one first seen at its address.</summary>
    public static SocketsHttpHandler CameraHandler()
    {
        var handler = GuardedHandler(IsCameraAddress, "isn't an address a camera can have");
#pragma warning disable S4830 // Deliberate: camera certificates are self-signed and can't chain to anything.
        handler.SslOptions.RemoteCertificateValidationCallback = (sender, certificate, _, _) =>
            LarisVMS.Core.Security.CameraCertificatePins.Shared.Check(sender, certificate);
#pragma warning restore S4830
        return handler;
    }

    private static readonly IPAddress CloudMetadataV4 = IPAddress.Parse("169.254.169.254");

    public static bool IsCameraAddress(IPAddress address)
    {
        address = Unwrap(address);
        if (address.Equals(CloudMetadataV4) || address.Equals(Ec2MetadataV6)) return false;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.Broadcast) || address.Equals(IPAddress.IPv6None))
            return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] != 0 && b[0] < 224;
        }
        return !address.IsIPv6Multicast;
    }

    private static SocketsHttpHandler GuardedHandler(Func<IPAddress, bool> allowed, string refusal) => new()
    {
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            // The machine's own outbound proxy (an admin's setting) may well be on the LAN.
            var isConfiguredProxy = context.InitialRequestMessage.RequestUri is { } requestUri
                && HttpClient.DefaultProxy.GetProxy(requestUri) is { } proxyUri
                && string.Equals(proxyUri.Host, context.DnsEndPoint.Host, StringComparison.OrdinalIgnoreCase);
            // Through the proxy, the proxy reaches the real destination, so check that instead.
            if (isConfiguredProxy && context.InitialRequestMessage.RequestUri is { } destination
                && !await DestinationAllowedAsync(destination.IdnHost, allowed, ct))
                throw new HttpRequestException($"{destination.Host} {refusal}.");
            var target = addresses.FirstOrDefault(a => isConfiguredProxy || allowed(a))
                ?? throw new HttpRequestException($"{context.DnsEndPoint.Host} {refusal}.");
            var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    /// <summary>The real destination of a request sent through a proxy: an address must be allowed,
    /// and a name must resolve only to allowed addresses. A name this machine can't resolve is left
    /// to the proxy (some networks only resolve outside names there).</summary>
    internal static async Task<bool> DestinationAllowedAsync(string host, Func<IPAddress, bool> allowed, CancellationToken ct)
    {
        var bare = host.Trim('[', ']');
        if (IPAddress.TryParse(bare, out var literal)) return allowed(literal);
        IPAddress[] resolved;
        try { resolved = await Dns.GetHostAddressesAsync(bare, ct); }
        catch (SocketException) { return true; }
        return resolved.All(allowed);
    }

    /// <summary>An IPv6 address that carries an IPv4 one (mapped ::ffff:a.b.c.d, NAT64 64:ff9b::/96,
    /// 6to4 2002::/16, or the old IPv4-compatible ::a.b.c.d) as that IPv4 address, so it's judged by
    /// where it really goes.</summary>
    internal static IPAddress Unwrap(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address;
        var b = address.GetAddressBytes();
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b.Skip(4).Take(8).All(x => x == 0))
            return new IPAddress(b[12..16]);
        if (b[0] == 0x20 && b[1] == 0x02)
            return new IPAddress(b[2..6]);
        if (b.Take(12).All(x => x == 0) && !(b[12] == 0 && b[13] == 0 && b[14] == 0 && b[15] <= 1))
            return new IPAddress(b[12..16]);
        return address;
    }

    // AWS's IPv6 instance metadata address; the IPv4 one (169.254.169.254) is link-local.
    private static readonly IPAddress Ec2MetadataV6 = IPAddress.Parse("fd00:ec2::254");

    /// <summary>This machine (unless <paramref name="allowLoopback"/>), unspecified, broadcast,
    /// link-local, or multicast — never a reasonable target for an admin-entered service URL.</summary>
    public static bool IsHostOrLinkLocal(IPAddress address, bool allowLoopback)
    {
        address = Unwrap(address);
        if (IPAddress.IsLoopback(address)) return !allowLoopback;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.Broadcast) || address.Equals(IPAddress.IPv6None))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 0 || (b[0] == 169 && b[1] == 254) || b[0] >= 224;
        }

        return address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.Equals(Ec2MetadataV6);
    }

    public static bool IsPublic(IPAddress address)
    {
        address = Unwrap(address);
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.Broadcast) || address.Equals(IPAddress.IPv6None))
            return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)    // carrier-grade NAT
                || (b[0] == 169 && b[1] == 254)                   // link-local
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19))    // benchmarking
                || b[0] >= 224);                                  // multicast and reserved
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                || (b[0] & 0xFE) == 0xFC);                        // unique local fc00::/7
        }

        return false;
    }
}
