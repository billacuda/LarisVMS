using System.Security.Claims;
using LarisVMS.Web.Auth;

namespace LarisVMS.Tests;

/// <summary>Google sign-in links only to an admin-entered email that Google says is verified, and
/// the optional domain list is enforced on the Workspace (<c>hd</c>) claim.</summary>
public class GoogleLinksTests
{
    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value))));

    [Fact]
    public void APendingKeyIsTheNormalizedEmail()
    {
        var key = GoogleLinks.PendingKey("  Boss@Example.COM ");
        Assert.Equal(GoogleLinks.PendingKey("boss@example.com"), key);
        Assert.True(GoogleLinks.IsPending(key));
        Assert.Equal("boss@example.com", GoogleLinks.PendingEmailOf(key));
        Assert.False(GoogleLinks.IsPending("108234567890123456789"));
        Assert.Null(GoogleLinks.PendingEmailOf("108234567890123456789"));
    }

    [Fact]
    public void OnlyAVerifiedEmailCounts()
    {
        Assert.Equal("boss@example.com",
            GoogleLinks.VerifiedEmailOf(Principal((ClaimTypes.Email, "Boss@Example.com"), ("email_verified", "true"))));
        Assert.Equal("boss@example.com",
            GoogleLinks.VerifiedEmailOf(Principal(("email", "boss@example.com"), ("email_verified", "True"))));
        Assert.Null(GoogleLinks.VerifiedEmailOf(Principal((ClaimTypes.Email, "boss@example.com"), ("email_verified", "false"))));
        Assert.Null(GoogleLinks.VerifiedEmailOf(Principal((ClaimTypes.Email, "boss@example.com"))));
        Assert.Null(GoogleLinks.VerifiedEmailOf(Principal(("email_verified", "true"))));
    }

    [Fact]
    public void BlankDomainsAllowEveryGoogleAccount()
    {
        Assert.True(GoogleLinks.IsDomainAllowed(null, Principal()));
        Assert.True(GoogleLinks.IsDomainAllowed("  \n ", Principal(("hd", "example.com"))));
    }

    [Fact]
    public void ADomainListRefusesGmailAndOtherDomains()
    {
        const string allowed = "Example.com\r\nbranch.example.org, other.net";
        Assert.True(GoogleLinks.IsDomainAllowed(allowed, Principal(("hd", "example.COM"))));
        Assert.True(GoogleLinks.IsDomainAllowed(allowed, Principal(("hd", "other.net"))));
        Assert.False(GoogleLinks.IsDomainAllowed(allowed, Principal(("hd", "evil.com"))));
        Assert.False(GoogleLinks.IsDomainAllowed(allowed, Principal((ClaimTypes.Email, "someone@gmail.com"))));
    }

    [Theory]
    [InlineData("example.com", true)]
    [InlineData("sub-domain.example.co.uk", true)]
    [InlineData("https://example.com", false)]
    [InlineData("user@example.com", false)]
    [InlineData("localhost", false)]
    [InlineData(".example.com", false)]
    public void DomainsMustBeBare(string value, bool expected) =>
        Assert.Equal(expected, GoogleLinks.LooksLikeDomain(value));

    [Theory]
    [InlineData("name@gmail.com", true)]
    [InlineData("first.last@company.example", true)]
    [InlineData("no-at-sign", false)]
    [InlineData("two@@example.com", false)]
    [InlineData("name@nodot", false)]
    [InlineData("name @gmail.com", false)]
    public void EmailsAreCheckedForShape(string value, bool expected) =>
        Assert.Equal(expected, GoogleLinks.LooksLikeEmail(value));

    [Fact]
    public void TheRejectionNamesNoAccount() =>
        Assert.DoesNotContain("@", GoogleLinks.NotLinkedMessage);
}
