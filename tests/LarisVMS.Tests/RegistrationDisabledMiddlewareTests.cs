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
}
