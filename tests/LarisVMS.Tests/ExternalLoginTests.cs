using System.Security.Claims;
using LarisVMS.Web.Auth;

namespace LarisVMS.Tests;

/// <summary>Entra sign-in links only by an admin-entered object ID, never by email.</summary>
public class ExternalLoginTests
{
    [Fact]
    public void TheObjectIdComesFromEitherClaimForm()
    {
        var oid = Guid.NewGuid();
        var longForm = new ClaimsPrincipal(new ClaimsIdentity([new Claim(EntraLinks.ObjectIdClaim, oid.ToString())]));
        var shortForm = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", oid.ToString())]));
        var emailOnly = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Email, "boss@example.com")]));

        Assert.Equal(oid, EntraLinks.ObjectIdOf(longForm));
        Assert.Equal(oid, EntraLinks.ObjectIdOf(shortForm));
        Assert.Null(EntraLinks.ObjectIdOf(emailOnly));
    }

    [Fact]
    public void APendingLinkIsKeyedByObjectIdAndTheRejectionNamesNoAccount()
    {
        var oid = Guid.NewGuid();
        Assert.True(EntraLinks.IsPending(EntraLinks.PendingKey(oid)));
        Assert.False(EntraLinks.IsPending("some-real-subject"));
        Assert.DoesNotContain("@", EntraLinks.NotLinkedMessage);
    }
}
