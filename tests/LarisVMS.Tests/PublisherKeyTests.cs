using System.Security.Cryptography;
using System.Text;
using LarisVMS.Core.Security;

namespace LarisVMS.Tests;

/// <summary>
/// The publisher check on updates (X2): with a publisher key built in, an update needs the
/// publisher's signature over exactly the payload the server offers, which a server holding only
/// its own key can't produce. Without one, nothing changes.
/// </summary>
public class PublisherKeyTests
{
    private static (string PublicPem, Func<string, string> Sign) NewKey()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ecdsa.ExportSubjectPublicKeyInfoPem(), payload => Convert.ToBase64String(
            ecdsa.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
    }

    private static readonly string Payload = UpdateSignature.UpdatePayload("win-x64", "0.221.0.300", "ABCDEF", null);

    [Fact]
    public void WithoutAPublisherKeyThereIsNoPublisherCheck() =>
        Assert.True(PublisherKey.Check(null, Payload, null, out _));

    [Fact]
    public void ThePublishersSignatureIsAccepted()
    {
        var (pem, sign) = NewKey();
        Assert.True(PublisherKey.Check(pem, Payload, sign(Payload), out var error), error);
    }

    [Fact]
    public void AnUnsignedUpdateIsRefused()
    {
        var (pem, _) = NewKey();
        Assert.False(PublisherKey.Check(pem, Payload, null, out var error));
        Assert.Contains("publisher", error);
    }

    [Fact]
    public void TheServersOwnKeyCantStandInForThePublishers()
    {
        var (publisher, _) = NewKey();
        var (_, server) = NewKey();
        Assert.False(PublisherKey.Check(publisher, Payload, server(Payload), out _));
    }

    [Fact]
    public void ASignatureForOneBuildDoesntCoverAnother()
    {
        var (pem, sign) = NewKey();
        var signature = sign(Payload);
        var otherBinary = UpdateSignature.UpdatePayload("win-x64", "0.221.0.300", "123456", null);
        var otherVision = UpdateSignature.UpdatePayload("win-x64", "0.221.0.300", "ABCDEF", "999");
        Assert.False(PublisherKey.Check(pem, otherBinary, signature, out _));
        Assert.False(PublisherKey.Check(pem, otherVision, signature, out _));
    }

    [Fact]
    public void TheBuildScriptsPayloadMatchesTheServers()
    {
        // build-node.ps1 writes this string by hand; it must stay the same as UpdatePayload.
        Assert.Equal("larisvms-update:v1|win-x64|0.221.0.300|abcdef|",
            UpdateSignature.UpdatePayload("win-x64", "0.221.0.300", "abcdef", null));
        Assert.Equal("larisvms-update:v1|proxy-win-x64|0.221.0.300|abcdef|",
            UpdateSignature.UpdatePayload("proxy-win-x64", "0.221.0.300", "abcdef", null));
    }
}
