using LarisVMS.Core.Entities;
using LarisVMS.Web.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace LarisVMS.Tests;

/// <summary>
/// AddDefaultUI registers the plain SignInManager again, and the last registration wins. Called
/// before it, AddSignInManager&lt;LarisSignInManager&gt; was overridden: every page asking for
/// LarisSignInManager (sign-in, two-factor, confirm password) failed with an InvalidCastException.
/// </summary>
public class IdentityRegistrationTests
{
    [Fact]
    public void TheSignInManagerIsLarisSignInManagerWhenRegisteredAfterTheDefaultUi()
    {
        var services = new ServiceCollection();
        services.AddIdentity<ApplicationUser, IdentityRole>()
            .AddDefaultUI()
            .AddSignInManager<LarisSignInManager>();

        var last = services.Last(s => s.ServiceType == typeof(SignInManager<ApplicationUser>));
        Assert.Equal(typeof(LarisSignInManager), last.ImplementationType);
    }

    [Fact]
    public void ProgramRegistersLarisSignInManagerAfterTheDefaultUi()
    {
        var program = File.ReadAllText(Path.Combine(FindWebProjectDirectory(), "Program.cs"));
        var defaultUi = program.IndexOf(".AddDefaultUI()", StringComparison.Ordinal);
        var signInManager = program.IndexOf(".AddSignInManager<LarisVMS.Web.Auth.LarisSignInManager>()", StringComparison.Ordinal);
        Assert.True(defaultUi >= 0 && signInManager >= 0, "Program.cs no longer has the Identity registration this checks.");
        Assert.True(signInManager > defaultUi, "AddSignInManager<LarisSignInManager>() must come after AddDefaultUI().");
    }

    private static string FindWebProjectDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "LarisVMS.Web");
            if (File.Exists(Path.Combine(candidate, "LarisVMS.Web.csproj"))) return candidate;
        }
        throw new DirectoryNotFoundException("src/LarisVMS.Web not found above the test output.");
    }
}
