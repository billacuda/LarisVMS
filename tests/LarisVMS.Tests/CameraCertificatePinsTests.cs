using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using LarisVMS.Core.Security;

namespace LarisVMS.Tests;

/// <summary>
/// Camera certificate pinning (X4): off changes nothing; on, the server remembers a camera address's
/// first certificate and refuses a different one later, while a node only enforces what it was given.
/// The last test connects to a real TLS listener to check the address reaches the pin check.
/// </summary>
public class CameraCertificatePinsTests
{
    private static X509Certificate2 NewCertificate(string name = "camera")
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        // Exported and re-imported so SslStream can use the private key on Windows.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    [Fact]
    public void OffAcceptsAnyCertificate()
    {
        var pins = new CameraCertificatePins();
        pins.Replace(false, new Dictionary<string, string> { ["10.0.0.5"] = "00" });
        Assert.True(pins.Check("10.0.0.5", NewCertificate()));
    }

    [Fact]
    public void OnTheServerRemembersTheFirstCertificateAndRefusesAnother()
    {
        var pins = new CameraCertificatePins();
        pins.Replace(true, []);
        var learned = new List<(string, string)>();
        var refused = new List<string>();
        pins.Learned += (h, t) => learned.Add((h, t));
        pins.Refused += (h, _, _) => refused.Add(h);
        var original = NewCertificate();

        Assert.True(pins.Check("Camera1.Local", original));
        Assert.True(pins.Check("camera1.local", original));
        Assert.False(pins.Check("camera1.local", NewCertificate()));

        Assert.Equal([("camera1.local", CameraCertificatePins.Thumbprint(original))], learned);
        Assert.Equal(["camera1.local"], refused);
    }

    [Fact]
    public void ANodeEnforcesTheServersPinsAndAcceptsAddressesTheServerHasntSeen()
    {
        var known = NewCertificate();
        var pins = new CameraCertificatePins { LearnUnknown = false };
        pins.Replace(true, new Dictionary<string, string> { ["10.0.0.5"] = CameraCertificatePins.Thumbprint(known).ToLowerInvariant() });

        Assert.True(pins.Check("10.0.0.5", known));
        Assert.False(pins.Check("10.0.0.5", NewCertificate()));
        Assert.True(pins.Check("10.0.0.6", NewCertificate()));
        Assert.True(pins.Check("10.0.0.6", NewCertificate())); // still not learned on a node
    }

    [Fact]
    public void WithoutAnAddressOrCertificateItsRefusedWhileOn()
    {
        var pins = new CameraCertificatePins();
        pins.Replace(true, []);
        Assert.False(pins.Check((string?)null, NewCertificate()));
        Assert.False(pins.Check("10.0.0.5", null));
    }

    [Theory]
    [InlineData("[FD00::5]", "fd00::5")]
    [InlineData("Camera.Example.com.", "camera.example.com")]
    [InlineData(" 10.0.0.5 ", "10.0.0.5")]
    [InlineData("", null)]
    public void AddressesAreComparedTheSameWayHoweverTheyreWritten(string host, string? normalized) =>
        Assert.Equal(normalized, CameraCertificatePins.Normalize(host));

    [Fact]
    public async Task TheServersCameraClientSeesTheCamerasAddress()
    {
        using var cert = NewCertificate();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var ssl = new SslStream(client.GetStream());
            await ssl.AuthenticateAsServerAsync(cert);
            var buffer = new byte[4096];
            _ = await ssl.ReadAsync(buffer);
            await ssl.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
        });

        var pins = new CameraCertificatePins();
        pins.Replace(true, []);
        string? seenHost = null;
        pins.Learned += (h, _) => seenHost = h;
        using var handler = new SocketsHttpHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = (sender, certificate, _, _) => pins.Check(sender, certificate);
        using var http = new HttpClient(handler);

        var response = await http.GetStringAsync($"https://127.0.0.1:{port}/");
        await serve;
        listener.Stop();

        Assert.Equal("ok", response);
        Assert.Equal("127.0.0.1", seenHost);
    }
}
