using System.Net;
using System.Net.Sockets;

namespace LarisVMS.Vision.Service;

/// <summary>Connects the external inference client only to addresses that aren't link-local
/// (169.254/16, fe80::/10), multicast, or a cloud metadata service, so a changed service address
/// can't send camera frames and the API key there.</summary>
public static class ExternalInferenceGuard
{
    private static readonly IPAddress Ec2MetadataV6 = IPAddress.Parse("fd00:ec2::254");

    public static bool IsAllowed(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.Broadcast) || address.Equals(IPAddress.IPv6None))
            return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] != 0 && !(b[0] == 169 && b[1] == 254) && b[0] < 224;
        }
        return !address.IsIPv6LinkLocal && !address.IsIPv6Multicast && !address.Equals(Ec2MetadataV6);
    }

    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        var target = addresses.FirstOrDefault(IsAllowed)
            ?? throw new HttpRequestException($"{context.DnsEndPoint.Host} is a link-local address.");
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
    }
}
