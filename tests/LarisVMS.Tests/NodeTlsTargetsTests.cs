using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using LarisVMS.Core.Security;
using LarisVMS.Node;

namespace LarisVMS.Tests;

/// <summary>
/// Encrypted connections from the server to a node (O4 + X6, opt-in per node): a node without it is
/// reached exactly as before; one with it goes to HTTPS on its own port, its certificate pinned, and
/// the token leaves the URL. The node's plain port then refuses everything but /health and loopback.
/// </summary>
public class NodeTlsTargetsTests
{
    private static X509Certificate2 NewCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=node", key, HashAlgorithmName.SHA256);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    [Fact]
    public void ANodeWithoutEncryptionIsReachedAsBefore()
    {
        var targets = new NodeTlsTargets();
        var uri = new Uri("http://10.0.0.5:8554/snapshot/abc?token=t1");
        Assert.Equal(uri, targets.Rewrite(uri));
    }

    [Fact]
    public void AnEncryptedNodeGoesToHttpsWithoutTheTokenInTheUrl()
    {
        var targets = new NodeTlsTargets();
        targets.Replace([new NodeTlsTargets.Target("10.0.0.5", 8554, 8555, "ab12")]);

        Assert.Equal("https://10.0.0.5:8555/playback-segment/c?path=x.mp4&seekSeconds=3",
            targets.Rewrite(new Uri("http://10.0.0.5:8554/playback-segment/c?path=x.mp4&token=t1&seekSeconds=3")).ToString());
        Assert.Equal("wss://10.0.0.5:8555/live/c?role=sub",
            targets.Rewrite(new Uri("ws://10.0.0.5:8554/live/c?token=t1&role=sub")).ToString());
        Assert.Equal("https://10.0.0.5:8555/reconcile-now",
            targets.Rewrite(new Uri("http://10.0.0.5:8554/reconcile-now?token=t1")).ToString());
        // Another port on the same address is another node, not set up for it.
        Assert.Equal("http://10.0.0.5:9000/x?token=t1", targets.Rewrite(new Uri("http://10.0.0.5:9000/x?token=t1")).ToString());
    }

    [Fact]
    public void AnIPv6NodeIsFoundAndCheckedWithOrWithoutBrackets()
    {
        // A node's stored address is bracketed (#4); a TLS connection's target host isn't.
        var known = NewCertificate();
        var targets = new NodeTlsTargets();
        targets.Replace([new NodeTlsTargets.Target("[FD00::5]", 8554, 8555, CameraCertificatePins.Thumbprint(known))]);

        Assert.Equal("https://[fd00::5]:8555/x", targets.Rewrite(new Uri("http://[fd00::5]:8554/x?token=t")).ToString());
        Assert.True(targets.Check("fd00::5", known));
        Assert.True(targets.Check("[fd00::5]", known));
    }

    [Fact]
    public void OnlyThePinnedCertificateIsAccepted()
    {
        var known = NewCertificate();
        var targets = new NodeTlsTargets();
        targets.Replace([new NodeTlsTargets.Target("10.0.0.5", 8554, 8555, CameraCertificatePins.Thumbprint(known).ToLowerInvariant())]);

        Assert.True(targets.Check("10.0.0.5", known));
        Assert.False(targets.Check("10.0.0.5", NewCertificate()));
        Assert.False(targets.Check("10.0.0.6", known));   // not an encrypted node: nothing to trust
        Assert.False(targets.Check("10.0.0.5", null));
    }

    [Fact]
    public async Task TheServersNodeClientReachesTheNodeOverPinnedHttps()
    {
        using var cert = NewCertificate();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var tlsPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        string? requestHead = null;
        var serve = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var ssl = new SslStream(client.GetStream());
            await ssl.AuthenticateAsServerAsync(cert);
            var buffer = new byte[4096];
            var read = await ssl.ReadAsync(buffer);
            requestHead = Encoding.ASCII.GetString(buffer, 0, read);
            await ssl.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
        });

        var targets = new NodeTlsTargets();
        targets.Replace([new NodeTlsTargets.Target("127.0.0.1", 8554, tlsPort, CameraCertificatePins.Thumbprint(cert))]);
        using var http = new HttpClient(targets.CreateHandler());
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:8554/snapshot/c?token=secret");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "secret");

        using var response = await http.SendAsync(request);
        await serve;
        listener.Stop();

        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
        Assert.StartsWith("GET /snapshot/c HTTP/1.1", requestHead);
        Assert.Contains("Authorization: Bearer secret", requestHead);
    }

    [Fact]
    public async Task ADifferentCertificateIsRefused()
    {
        using var cert = NewCertificate();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var tlsPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var ssl = new SslStream(client.GetStream());
            try { await ssl.AuthenticateAsServerAsync(cert); } catch (Exception) { }
        });

        var targets = new NodeTlsTargets();
        targets.Replace([new NodeTlsTargets.Target("127.0.0.1", 8554, tlsPort, CameraCertificatePins.Thumbprint(NewCertificate()))]);
        using var http = new HttpClient(targets.CreateHandler());

        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("http://127.0.0.1:8554/snapshot/c"));
        await serve;
        listener.Stop();
    }

    [Theory]
    [InlineData(false, true, 8554, "10.0.0.9", "/live/c", false)]   // off: as before
    [InlineData(true, false, 8554, "10.0.0.9", "/live/c", false)]   // no HTTPS endpoint yet: plain still serves
    [InlineData(true, true, 8554, "10.0.0.9", "/live/c", true)]
    [InlineData(true, true, 8554, "10.0.0.9", "/snapshot/c", true)]
    [InlineData(true, true, 8554, "10.0.0.9", "/health", false)]   // failover probes
    [InlineData(true, true, 8554, "127.0.0.1", "/detections", false)] // this machine's Vision Service
    [InlineData(true, true, 8555, "10.0.0.9", "/live/c", false)]    // the HTTPS port itself
    public void ThePlainPortRefusesMediaOnceEncrypted(bool encryptedOnly, bool httpsRunning, int localPort, string remote, string path, bool refused) =>
        Assert.Equal(refused, ServerConnectionPolicy.ShouldRefuse(encryptedOnly, httpsRunning, localPort, 8554, IPAddress.Parse(remote), path));
}
