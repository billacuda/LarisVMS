using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LarisVMS.Relay;
using Microsoft.Extensions.Logging.Abstractions;

namespace LarisVMS.Tests;

public sealed class CertHolderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "laris-certholder-" + Guid.NewGuid().ToString("N"));
    private readonly string _pfx;

    public CertHolderTests()
    {
        Directory.CreateDirectory(_dir);
        _pfx = Path.Combine(_dir, "real.pfx");
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=real", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        File.WriteAllBytes(_pfx, cert.Export(X509ContentType.Pkcs12, "secret"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private CertHolder Holder(string path, string password) => new(
        new CertHolderOptions(path, password, AllowInsecure: true, "localhost", Path.Combine(_dir, "self.pfx")),
        NullLogger.Instance);

    [Fact]
    public void QuotedPath_LoadsTheRealCertificate()
    {
        var holder = Holder("  \"" + _pfx + "\" ", "secret");
        Assert.True(holder.Load());
        Assert.False(holder.IsSelfSigned);
        Assert.Equal("CN=real", holder.Current!.Subject);
    }

    [Fact]
    public void WrongPassword_FallsBackToSelfSignedInsteadOfThrowing()
    {
        var holder = Holder(_pfx, "wrong");
        Assert.True(holder.Load());
        Assert.True(holder.IsSelfSigned);
        Assert.Contains("Could not load pfx", holder.LastError);
    }
}
