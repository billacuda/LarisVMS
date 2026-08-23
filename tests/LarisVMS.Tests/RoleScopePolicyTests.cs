using LarisVMS.Core.Enums;
using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

public class RoleScopePolicyTests
{
    [Fact]
    public void OrgTierRoleCanBeGrantedEveryCamera()
    {
        Assert.True(RoleScopePolicy.CanGrant(RoleScopeTier.Org, CameraAccessScopeType.All, targetIsSite: false));
    }

    [Fact]
    public void SiteTierRoleCannotBeGrantedEveryCamera()
    {
        Assert.False(RoleScopePolicy.CanGrant(RoleScopeTier.Site, CameraAccessScopeType.All, targetIsSite: false));
    }

    [Fact]
    public void GroupTierRoleCannotBeGrantedEveryCamera()
    {
        Assert.False(RoleScopePolicy.CanGrant(RoleScopeTier.Group, CameraAccessScopeType.All, targetIsSite: false));
    }

    [Fact]
    public void SiteTierRoleCanBeGrantedABareSite()
    {
        Assert.True(RoleScopePolicy.CanGrant(RoleScopeTier.Site, CameraAccessScopeType.Group, targetIsSite: true));
    }

    [Fact]
    public void GroupTierRoleCannotBeGrantedABareSite()
    {
        Assert.False(RoleScopePolicy.CanGrant(RoleScopeTier.Group, CameraAccessScopeType.Group, targetIsSite: true));
    }

    [Fact]
    public void GroupTierRoleCanBeGrantedANonSiteGroup()
    {
        Assert.True(RoleScopePolicy.CanGrant(RoleScopeTier.Group, CameraAccessScopeType.Group, targetIsSite: false));
    }

    [Theory]
    [InlineData(RoleScopeTier.Org)]
    [InlineData(RoleScopeTier.Site)]
    [InlineData(RoleScopeTier.Group)]
    public void EveryTierCanBeGrantedASingleCameraRegardlessOfSite(RoleScopeTier tier)
    {
        Assert.True(RoleScopePolicy.CanGrant(tier, CameraAccessScopeType.Camera, targetIsSite: false));
        Assert.True(RoleScopePolicy.CanGrant(tier, CameraAccessScopeType.Camera, targetIsSite: true));
    }

    [Fact]
    public void AnOrgTierActorCanAssignAnyRole()
    {
        Assert.True(RoleScopePolicy.CanAssignRole(RoleScopeTier.Org, RoleScopeTier.Org));
        Assert.True(RoleScopePolicy.CanAssignRole(RoleScopeTier.Org, RoleScopeTier.Site));
        Assert.True(RoleScopePolicy.CanAssignRole(RoleScopeTier.Org, RoleScopeTier.Group));
    }

    [Fact]
    public void ASiteOrGroupTierActorCannotAssignAnOrgTierRole()
    {
        Assert.False(RoleScopePolicy.CanAssignRole(RoleScopeTier.Site, RoleScopeTier.Org));
        Assert.False(RoleScopePolicy.CanAssignRole(RoleScopeTier.Group, RoleScopeTier.Org));
    }

    [Fact]
    public void ASiteOrGroupTierActorCanAssignNonOrgTierRoles()
    {
        Assert.True(RoleScopePolicy.CanAssignRole(RoleScopeTier.Site, RoleScopeTier.Site));
        Assert.True(RoleScopePolicy.CanAssignRole(RoleScopeTier.Site, RoleScopeTier.Group));
        Assert.True(RoleScopePolicy.CanAssignRole(RoleScopeTier.Group, RoleScopeTier.Site));
        Assert.True(RoleScopePolicy.CanAssignRole(RoleScopeTier.Group, RoleScopeTier.Group));
    }
}
