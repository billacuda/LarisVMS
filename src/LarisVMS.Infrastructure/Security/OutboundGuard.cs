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
    public static SocketsHttpHandler PublicOnlyHandler() => new()
    {
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            // The machine's own outbound proxy (an admin's setting) may well be on the LAN.
            var isConfiguredProxy = context.InitialRequestMessage.RequestUri is { } requestUri
                && HttpClient.DefaultProxy.GetProxy(requestUri) is { } proxyUri
                && string.Equals(proxyUri.Host, context.DnsEndPoint.Host, StringComparison.OrdinalIgnoreCase);
            var target = addresses.FirstOrDefault(a => isConfiguredProxy || IsPublic(a))
                ?? throw new HttpRequestException($"{context.DnsEndPoint.Host} isn't a public address.");
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

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
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
