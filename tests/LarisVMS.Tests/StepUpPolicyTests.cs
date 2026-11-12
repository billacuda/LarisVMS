using Microsoft.AspNetCore.Http;
using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

public class StepUpPolicyTests
{
    [Theory]
    [InlineData("GET", "/Admin/Settings/Security", true)]
    [InlineData("POST", "/Admin/Settings/Permissions/Users", true)]
    [InlineData("GET", "/admin/apikeys", true)]
    [InlineData("POST", "/api/encryption/backup", true)]
    [InlineData("DELETE", "/api/admin/nodes/123", true)]
    [InlineData("GET", "/api/admin/nodes", false)]
    [InlineData("GET", "/", false)]
    [InlineData("GET", "/Cameras", false)]
    [InlineData("POST", "/api/cameras/1/ptz/move", false)]
    [InlineData("GET", "/Administrator", false)]
    public void AdminPagesAndAdminChangesNeedARecentConfirmation(string method, string path, bool isProtected) =>
        Assert.Equal(isProtected, StepUpPolicy.IsProtected(method, new PathString(path)));

    [Fact]
    public void AConfirmationLastsUntilItsWindowEnds()
    {
        var now = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(StepUpPolicy.IsConfirmed(null, now));
        Assert.False(StepUpPolicy.IsConfirmed(now, now));
        Assert.True(StepUpPolicy.IsConfirmed(now.AddSeconds(1), now));
    }

    [Fact]
    public void UsingAdminPagesExtendsTheWindowAtMostOnceAMinute()
    {
        var now = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(StepUpPolicy.ShouldExtend(now + StepUpPolicy.Window, now));
        Assert.False(StepUpPolicy.ShouldExtend(now + StepUpPolicy.Window - TimeSpan.FromSeconds(30), now));
        Assert.True(StepUpPolicy.ShouldExtend(now + StepUpPolicy.Window - TimeSpan.FromMinutes(2), now));
    }
}
