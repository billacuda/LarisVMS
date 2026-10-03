using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace FakeOnvif;

/// <summary>
/// Answers WS-Discovery Probes (what LarisVMS's Cameras → Discover sends) for every fake camera.
/// Listens on 239.255.255.250:3702 on the demo adapter only, and sends one ProbeMatches per camera
/// from that camera's own IP, since LarisVMS (LarisVMS.Onvif.Discovery.WsDiscoveryClient) reads one
/// ProbeMatch per datagram and records the sender address.
/// </summary>
public sealed class WsDiscoveryResponder(DemoConfig config, ILogger<WsDiscoveryResponder> logger) : BackgroundService
{
    private static readonly IPAddress Group = IPAddress.Parse("239.255.255.250");
    private const int Port = 3702;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // Windows' own Function Discovery service also listens on 3702.
        listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Bind(new IPEndPoint(IPAddress.Any, Port));
        listener.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
            new MulticastOption(Group, IPAddress.Parse(config.Network.HostIp)));
        logger.LogInformation("WS-Discovery responder listening on {Ip}", config.Network.HostIp);

        var buffer = new byte[65536];
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try { received = await listener.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct); }
            catch (OperationCanceledException) { break; }

            var remote = (IPEndPoint)received.RemoteEndPoint;
            // Only answer probes from the demo subnet, never from the real LAN.
            if (!InDemoSubnet(remote.Address)) continue;

            string? messageId;
            try { messageId = ParseProbe(Encoding.UTF8.GetString(buffer, 0, received.ReceivedBytes)); }
            catch { continue; }
            if (messageId is null) continue;

            logger.LogInformation("Probe from {Remote}; answering for {Count} cameras", remote, config.Cameras.Count);
            foreach (var camera in config.Cameras)
            {
                try
                {
                    using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    sender.Bind(new IPEndPoint(IPAddress.Parse(camera.Ip), 0));
                    await sender.SendToAsync(Encoding.UTF8.GetBytes(ProbeMatch(camera, messageId)), SocketFlags.None, remote, ct);
                }
                catch (Exception ex) { logger.LogWarning(ex, "Could not answer the probe for {Camera}", camera.Id); }
            }
        }
    }

    private bool InDemoSubnet(IPAddress address)
    {
        var mask = uint.MaxValue << (32 - config.Network.PrefixLength);
        static uint ToUInt(IPAddress a) { var b = a.GetAddressBytes(); return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }
        return (ToUInt(address) & mask) == (ToUInt(IPAddress.Parse(config.Network.HostIp)) & mask);
    }

    /// <summary>The Probe's MessageID if this is a Probe for video devices (or for any type), else null.</summary>
    private static string? ParseProbe(string xml)
    {
        var doc = XDocument.Parse(xml);
        var probe = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Probe");
        if (probe is null) return null;
        var types = probe.Elements().FirstOrDefault(e => e.Name.LocalName == "Types")?.Value ?? "";
        if (types.Length > 0 && !types.Contains("NetworkVideoTransmitter", StringComparison.OrdinalIgnoreCase)) return null;
        return doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "MessageID")?.Value.Trim();
    }

    private static string ProbeMatch(CameraConfig c, string relatesTo) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"
                    xmlns:a="http://schemas.xmlsoap.org/ws/2004/08/addressing"
                    xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery"
                    xmlns:dn="http://www.onvif.org/ver10/network/wsdl">
          <s:Header>
            <a:MessageID>uuid:{Guid.NewGuid()}</a:MessageID>
            <a:RelatesTo>{Soap.Esc(relatesTo)}</a:RelatesTo>
            <a:To>http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</a:To>
            <a:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/ProbeMatches</a:Action>
          </s:Header>
          <s:Body>
            <d:ProbeMatches>
              <d:ProbeMatch>
                <a:EndpointReference><a:Address>urn:uuid:{c.Uuid}</a:Address></a:EndpointReference>
                <d:Types>dn:NetworkVideoTransmitter</d:Types>
                <d:Scopes>onvif://www.onvif.org/type/video_encoder onvif://www.onvif.org/Profile/Streaming onvif://www.onvif.org/hardware/{Uri.EscapeDataString(c.Model)} onvif://www.onvif.org/name/{Uri.EscapeDataString(c.Name)}</d:Scopes>
                <d:XAddrs>{c.DeviceServiceUrl}</d:XAddrs>
                <d:MetadataVersion>1</d:MetadataVersion>
              </d:ProbeMatch>
            </d:ProbeMatches>
          </s:Body>
        </s:Envelope>
        """;
}
