using Microsoft.AspNetCore.Http;
using LarisVMS.Web.Middleware;
using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

public class PasswordPolicyTests
{
    [Theory]
    [InlineData("password1234")]
    [InlineData("PASSWORD1234")]
    [InlineData("123456789012")]
    [InlineData("987654321098")]
    [InlineData("abcdefghijklm")]
    [InlineData("aaaaaaaaaaaa")]
    [InlineData("abababababab")]
    [InlineData("LarisVMS1234")]
    public void CommonAndPredictablePasswordsAreRefused(string password) =>
        Assert.Equal("common", CommonPasswordValidator.Check(password, "jane@example.com", "jane@example.com", null));

    [Theory]
    [InlineData("jane.doe-rocks-2026", "jane.doe@example.com", null)]
    [InlineData("my-SMITHERS-camera", "js@example.com", "Jane Smithers")]
    public void PasswordsBuiltFromTheAccountsNameAreRefused(string password, string email, string? displayName) =>
        Assert.Equal("containsName", CommonPasswordValidator.Check(password, email, email, displayName));

    [Theory]
    [InlineData("correct horse battery staple")]
    [InlineData("k3!vR9#pQ2zM")]
    public void OrdinaryLongPasswordsPass(string password) =>
        Assert.Null(CommonPasswordValidator.Check(password, "jane@example.com", "jane@example.com", "Jane Doe"));

    [Fact]
    public void TheAdminsOwnBlocklistIsUsed()
    {
        var extra = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "correct horse battery staple" };
        Assert.Equal("common", CommonPasswordValidator.Check("Correct Horse Battery Staple", "a@b.c", "a@b.c", null, extra));
    }

    [Theory]
    [InlineData("GET", "/Identity/Account/ChangePassword", true)]
    [InlineData("POST", "/Identity/Account/ChangePassword", true)]
    [InlineData("POST", "/Identity/Account/Logout", true)]
    [InlineData("POST", "/Identity/Account/TwoFactor", true)]
    [InlineData("GET", "/css/site.css", true)]
    [InlineData("GET", "/_framework/blazor.web.js", true)]
    [InlineData("GET", "/", false)]
    [InlineData("GET", "/Cameras", false)]
    [InlineData("POST", "/Admin/Settings/Users", false)]
    [InlineData("GET", "/api/cameras/123/snapshot.jpg", false)]
    [InlineData("POST", "/js/site.js", false)]
    [InlineData("GET", "/locales/es-AR.js", true)]
    [InlineData("GET", "/lib/bootstrap/css/bootstrap.min.css", true)]
    [InlineData("GET", "/admin/locales/fr.json", false)]   // a file extension alone isn't enough
    [InlineData("GET", "/Help/some.page", false)]
    [InlineData("GET", "/api/nodes/download/x.exe", false)]
    public void UntilTheAccountIsSetUpOnlyItsPagesWork(string method, string path, bool allowed) =>
        Assert.Equal(allowed, AccountSetupRequiredMiddleware.IsAllowed(method, new PathString(path)));
}
