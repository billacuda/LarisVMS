using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LarisVMS.Core.Entities;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Web.Auth;

namespace LarisVMS.Tests;

/// <summary>
/// The authenticator code check matches the standard (RFC 6238's test values) and authenticator apps'
/// Base32 keys, and accepts a code only within two steps of now.
/// </summary>
public class SingleUseAuthenticatorTests
{
    private static readonly byte[] RfcKey = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, 287082)]          // RFC 6238 SHA-1 vectors, last six digits
    [InlineData(1111111109L, 81804)]
    [InlineData(1234567890L, 5924)]
    [InlineData(2000000000L, 279037)]
    public void CodesMatchTheStandard(long unixSeconds, int expected) =>
        Assert.Equal(expected, SingleUseAuthenticatorTokenProvider.Code(RfcKey,
            SingleUseAuthenticatorTokenProvider.CurrentStep(DateTimeOffset.FromUnixTimeSeconds(unixSeconds))));

    [Fact]
    public void KeysAreReadAsAuthenticatorAppsWriteThem() =>
        Assert.Equal(RfcKey, SingleUseAuthenticatorTokenProvider.Base32Decode("gezd gnbv gy3t qojq gezd gnbv gy3t qojq".Replace(" ", "")));

    [Fact]
    public async Task ACodeWorksOnlyOnce()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddIdentityCore<ApplicationUser>().AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>().AddDefaultTokenProviders()
            .AddTokenProvider<SingleUseAuthenticatorTokenProvider>(TokenOptions.DefaultAuthenticatorProvider);
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = "alice@example.com", Email = "alice@example.com" };
        Assert.True((await users.CreateAsync(user)).Succeeded);
        await users.ResetAuthenticatorKeyAsync(user);
        var key = SingleUseAuthenticatorTokenProvider.Base32Decode((await users.GetAuthenticatorKeyAsync(user))!);
        var now = SingleUseAuthenticatorTokenProvider.CurrentStep(DateTimeOffset.UtcNow);
        string CodeFor(long step) => SingleUseAuthenticatorTokenProvider.Code(key, step).ToString("D6");
        var authenticator = users.Options.Tokens.AuthenticatorTokenProvider;

        Assert.True(await users.VerifyTwoFactorTokenAsync(user, authenticator, CodeFor(now)));
        Assert.False(await users.VerifyTwoFactorTokenAsync(user, authenticator, CodeFor(now)));      // the same code again
        Assert.False(await users.VerifyTwoFactorTokenAsync(user, authenticator, CodeFor(now - 1)));  // an older one
        Assert.True(await users.VerifyTwoFactorTokenAsync(user, authenticator, CodeFor(now + 1)));   // the next one is fine
    }

    [Fact]
    public void OnlyCodesNearNowAreAccepted()
    {
        var now = SingleUseAuthenticatorTokenProvider.CurrentStep(DateTimeOffset.UtcNow);
        Assert.Equal(now - 2, SingleUseAuthenticatorTokenProvider.MatchingStep(RfcKey, SingleUseAuthenticatorTokenProvider.Code(RfcKey, now - 2), now));
        Assert.Null(SingleUseAuthenticatorTokenProvider.MatchingStep(RfcKey, SingleUseAuthenticatorTokenProvider.Code(RfcKey, now - 10), now));
    }
}
