using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using LarisVMS.Core.Setup;
using LarisVMS.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;

namespace LarisVMS.Tests;

/// <summary>The messages a node or proxy prints when its first registration fails, and telling an
/// unreadable secret in the database apart from other failures.</summary>
public class RegistrationFailureTests
{
    private const string Url = "https://vms.example.com:8444";

    [Theory]
    [InlineData("https://vms.example.com:8444")]
    [InlineData("https://192.168.1.10:8444/")]
    [InlineData("http://vms")]
    public void AGoodServerAddressPasses(string url) => Assert.Null(RegistrationFailure.CheckServerUrl(url));

    [Theory]
    [InlineData("https:/vms.example.com:8444")]
    [InlineData("https://")]
    [InlineData("https://<server address>:8444")]
    [InlineData("192.168.1.10:8444")]
    [InlineData("vms.example.com:8444")]
    [InlineData("ftp://vms.example.com")]
    [InlineData("")]
    public void ABadServerAddressIsNamed(string url) =>
        Assert.Contains($"\"{url}\"", RegistrationFailure.CheckServerUrl(url));

    [Fact]
    public void FailuresAreDescribed()
    {
        Assert.Contains("Connection refused", RegistrationFailure.Describe(
            new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused (vms.example.com:8444)"), Url));
        Assert.Contains("DNS", RegistrationFailure.Describe(
            new HttpRequestException(HttpRequestError.NameResolutionError, "Name or service not known"), Url));
        Assert.Contains("HTTPS port", RegistrationFailure.Describe(
            new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established"), Url));
        Assert.Contains("registration key", RegistrationFailure.Describe(
            new HttpRequestException("401", null, HttpStatusCode.Unauthorized), Url));
        Assert.Contains("too many attempts", RegistrationFailure.Describe(
            new HttpRequestException("429", null, HttpStatusCode.TooManyRequests), Url));
        Assert.Contains("HTTP 500", RegistrationFailure.Describe(
            new HttpRequestException("500", null, HttpStatusCode.InternalServerError), Url));
        Assert.Contains("in time", RegistrationFailure.Describe(new TaskCanceledException(), Url));
        Assert.Contains("not like a LarisVMS server", RegistrationFailure.Describe(new JsonException(), Url));
        Assert.Null(RegistrationFailure.Describe(new InvalidOperationException("a bug"), Url));
    }

    [Fact]
    public void NotRegisteredFitsWhereItRuns()
    {
        Assert.Contains("container's environment", RegistrationFailure.NotRegistered("node", isWindows: false, inContainer: true));
        Assert.Contains("--server-url", RegistrationFailure.NotRegistered("node", isWindows: true, inContainer: false));
        Assert.Contains("sudo larisvms-proxy setup", RegistrationFailure.NotRegistered("proxy", isWindows: false, inContainer: false));
    }

    [Fact]
    public void ASecretFromAnotherKeyRingIsUnreadable()
    {
        var stored = "enc:v1:" + new EphemeralDataProtectionProvider().CreateProtector("test").Protect("secret");
        var other = new EphemeralDataProtectionProvider().CreateProtector("test");

        var ex = Assert.Throws<SecretUnreadableException>(() => SecretProtection.Unprotect(stored, other));
        Assert.Throws<SecretUnreadableException>(() => SecretProtection.UnprotectStrict(stored, other));
        Assert.True(SecretUnreadableException.IsIn(ex));
        Assert.True(SecretUnreadableException.IsIn(new InvalidOperationException("wrapped", ex)));
        Assert.False(SecretUnreadableException.IsIn(new CryptographicException("something else")));
        Assert.False(SecretUnreadableException.IsIn(null));
    }

    [Fact]
    public void PlainAndEmptyValuesPassThrough()
    {
        var protector = new EphemeralDataProtectionProvider().CreateProtector("test");
        Assert.Equal("plain", SecretProtection.Unprotect("plain", protector));
        Assert.Null(SecretProtection.Unprotect(null, protector));
        Assert.Equal("secret", SecretProtection.Unprotect("enc:v1:" + protector.Protect("secret"), protector));
    }
}
