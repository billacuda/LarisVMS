using Microsoft.AspNetCore.Http;
using LarisVMS.Web.Middleware;

namespace LarisVMS.Tests;

public class RegistrationDisabledMiddlewareTests
{
    [Theory]
    [InlineData("/Identity/Account/Register")]
    [InlineData("/identity/account/register")]
    [InlineData("/Identity/Account/Register/")]
    public void BlocksTheRegisterPageRegardlessOfCasingOrTrailingSlash(string path)
    {
        Assert.True(RegistrationDisabledMiddleware.IsRegisterPath(new PathString(path)));
    }

    [Theory]
    [InlineData("/Identity/Account/Login")]
    [InlineData("/Identity/Account/Manage")]
    [InlineData("/")]
    [InlineData("/Identity/Account/RegisterConfirmation")]
    public void LeavesEveryOtherPathAlone(string path)
    {
        Assert.False(RegistrationDisabledMiddleware.IsRegisterPath(new PathString(path)));
    }

    [Theory]
    [InlineData("/Identity/Account/Register", true)]
    [InlineData("/Identity/Account/RegisterConfirmation", true)]
    [InlineData("/Identity/Account/ForgotPassword", true)]
    [InlineData("/Identity/Account/ResetPassword", true)]
    [InlineData("/Identity/Account/Manage", true)]
    [InlineData("/Identity/Account/Manage/DeletePersonalData", true)]
    [InlineData("/Identity/Account/Login", false)]
    [InlineData("/identity/account/logout", false)]
    [InlineData("/Identity/Account/AccessDenied", false)]
    [InlineData("/Identity/Account/ExternalLogin", false)]
    [InlineData("/Identity/Account/ChangePassword", false)]
    [InlineData("/Identity/Account/TwoFactor", false)]
    [InlineData("/Identity/Account/LoginWith2fa", false)]
    [InlineData("/Identity/Account/Sessions", false)]
    [InlineData("/Identity/Account/Confirm", false)]
    [InlineData("/Identity/Account/LoginWithRecoveryCode", true)]
    [InlineData("/Identity/Account/Manage/TwoFactorAuthentication", true)]
    [InlineData("/Identity/Account/Manage/ChangePassword", true)]
    [InlineData("/Identity/lib/bootstrap/dist/css/bootstrap.min.css", false)]
    [InlineData("/Cameras", false)]
    public void OnlyThePagesLarisVmsUsesAreReachableUnderIdentity(string path, bool blocked)
    {
        Assert.Equal(blocked, RegistrationDisabledMiddleware.IsBlockedIdentityPath(new PathString(path)));
    }
}
