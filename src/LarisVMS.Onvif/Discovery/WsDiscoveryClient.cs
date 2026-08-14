using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace LarisVMS.Onvif.Discovery;

/// <summary>
/// WS-Discovery LAN auto-scan: sends a multicast Probe for NetworkVideoTransmitter to
/// 239.255.255.250:3702 and collects ProbeMatch responses for the given window. This only finds
/// cameras on the same broadcast domain as this host/node — cameras on a different VLAN need the
/// subnet-sweep fallback (not yet implemented; see the plan's M2 scope) or manual entry via
/// DeviceServiceUri.
/// </summary>
public static class WsDiscoveryClient
{
    private static readonly IPEndPoint MulticastEndpoint = new(IPAddress.Parse("239.255.255.250"), 3702);

    public static async Task<IReadOnlyList<DiscoveredDevice>> ProbeAsync(
        TimeSpan timeout, CancellationToken ct = default)
    {
        var results = new Dictionary<string, DiscoveredDevice>(); // keyed by EndpointReference, de-duplicates
        // responses received once per listening socket.
        var messageId = $"uuid:{Guid.NewGuid()}";
        var probe = BuildProbeMessage(messageId);
        var probeBytes = Encoding.UTF8.GetBytes(probe);

        var sockets = CreateSocketsForActiveInterfaces();
        if (sockets.Count == 0)
        {
            // Fall back to a single unbound socket if no multicast-capable interface was found —
            // still works on hosts with a simple single-NIC configuration.
            var fallback = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            fallback.Bind(new IPEndPoint(IPAddress.Any, 0));
            sockets.Add(fallback);
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            foreach (var socket in sockets)
            {
                try { await socket.SendToAsync(probeBytes, SocketFlags.None, MulticastEndpoint, ct); }
                catch { /* interface may not support sending to this multicast group — skip it */ }
            }

            var receiveTasks = sockets.Select(s => ReceiveLoopAsync(s, results, timeoutCts.Token)).ToArray();
            try { await Task.WhenAll(receiveTasks); }
            catch (OperationCanceledException) { /* expected once the timeout fires */ }
        }
        finally
        {
            foreach (var socket in sockets) socket.Dispose();
        }

        return results.Values.ToList();
    }

    private static async Task ReceiveLoopAsync(Socket socket, Dictionary<string, DiscoveredDevice> results, CancellationToken ct)
    {
        var buffer = new byte[65536];
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await socket.ReceiveFromAsync(
                    buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct);
            }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { return; }

            var remoteAddress = (received.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
            try
            {
                var device = ParseProbeMatch(buffer.AsSpan(0, received.ReceivedBytes), remoteAddress);
                if (device is not null)
                    results[device.EndpointReference] = device;
            }
            catch { /* malformed or unrelated multicast traffic on the same port — ignore */ }
        }
    }

    private static List<Socket> CreateSocketsForActiveInterfaces()
    {
        var sockets = new List<Socket>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (!nic.Supports(NetworkInterfaceComponent.IPv4)) continue;

            var ipProps = nic.GetIPProperties();
            var ipv4 = ipProps.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
            if (ipv4 is null) continue;

            try
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.Bind(new IPEndPoint(ipv4, 0));
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                    ipv4.GetAddressBytes());
                sockets.Add(socket);
            }
            catch { /* interface doesn't actually support what it advertises — skip it */ }
        }

        return sockets;
    }

    private static string BuildProbeMessage(string messageId) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <e:Envelope xmlns:e="http://www.w3.org/2003/05/soap-envelope"
                    xmlns:w="http://schemas.xmlsoap.org/ws/2004/08/addressing"
                    xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery"
                    xmlns:dn="http://www.onvif.org/ver10/network/wsdl">
          <e:Header>
            <w:MessageID>{messageId}</w:MessageID>
            <w:To e:mustUnderstand="1">urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To>
            <w:Action e:mustUnderstand="1">http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action>
          </e:Header>
          <e:Body>
            <d:Probe>
              <d:Types>dn:NetworkVideoTransmitter</d:Types>
            </d:Probe>
          </e:Body>
        </e:Envelope>
        """;

    private static DiscoveredDevice? ParseProbeMatch(ReadOnlySpan<byte> bytes, string remoteAddress)
    {
        var doc = XDocument.Parse(Encoding.UTF8.GetString(bytes));
        XNamespace soap = "http://www.w3.org/2003/05/soap-envelope";
        XNamespace wsa = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
        XNamespace d = "http://schemas.xmlsoap.org/ws/2005/04/discovery";

        var probeMatch = doc.Descendants(d + "ProbeMatch").FirstOrDefault();
        if (probeMatch is null) return null;

        var epr = probeMatch.Descendants(wsa + "Address").FirstOrDefault()?.Value.Trim()
                  ?? probeMatch.Descendants(d + "EndpointReference").FirstOrDefault()?.Value.Trim()
                  ?? Guid.NewGuid().ToString();

        var xAddrsRaw = probeMatch.Element(d + "XAddrs")?.Value ?? string.Empty;
        var xAddrs = xAddrsRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Uri.TryCreate(s, UriKind.Absolute, out var u) ? u : null)
            .Where(u => u is not null)
            .Select(u => u!)
            .ToList();
        if (xAddrs.Count == 0) return null;

        var typesRaw = probeMatch.Element(d + "Types")?.Value ?? string.Empty;
        var types = typesRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        var scopesRaw = probeMatch.Element(d + "Scopes")?.Value ?? string.Empty;
        var scopes = scopesRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        return new DiscoveredDevice(epr, xAddrs, types, scopes, remoteAddress);
    }
}
