using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

public class RoleManagementPolicyTests
{
    [Fact]
    public void TheSuperAdminTaggedRoleCannotBeRenamed()
    {
        Assert.False(RoleManagementPolicy.CanRename("SUPER"));
    }

    [Fact]
    public void AnyOtherTagOrNoTagCanBeRenamed()
    {
        Assert.True(RoleManagementPolicy.CanRename("VIEWER"));
        Assert.True(RoleManagementPolicy.CanRename(null));
    }

    [Fact]
    public void TheSuperAdminTaggedRoleCanNeverBeDeletedEvenWithNoUsers()
    {
        Assert.False(RoleManagementPolicy.CanDelete("SUPER", userCount: 0));
    }

    [Fact]
    public void ARoleStillHoldingUsersCannotBeDeleted()
    {
        Assert.False(RoleManagementPolicy.CanDelete("OPERATOR", userCount: 3));
    }

    [Fact]
    public void ARoleWithNoUsersCanBeDeleted()
    {
        Assert.True(RoleManagementPolicy.CanDelete("OPERATOR", userCount: 0));
    }

    [Fact]
    public void ACustomRoleWithNoTagCanBeDeletedOnceEmpty()
    {
        Assert.True(RoleManagementPolicy.CanDelete(null, userCount: 0));
    }

    [Fact]
    public void DiffPermissionsAddsWhatsCheckedButNotYetARow()
    {
        var (toAdd, toRemove) = RoleManagementPolicy.DiffPermissions(
            existing: [],
            submitted: [("Cameras", "View")]);

        Assert.Equal([("Cameras", "View")], toAdd);
        Assert.Empty(toRemove);
    }

    [Fact]
    public void DiffPermissionsRemovesWhatsARowButNoLongerChecked()
    {
        var (toAdd, toRemove) = RoleManagementPolicy.DiffPermissions(
            existing: [("Cameras", "View")],
            submitted: []);

        Assert.Empty(toAdd);
        Assert.Equal([("Cameras", "View")], toRemove);
    }

    [Fact]
    public void DiffPermissionsLeavesAPairPresentInBothUntouched()
    {
        var (toAdd, toRemove) = RoleManagementPolicy.DiffPermissions(
            existing: [("Cameras", "View"), ("Playback", "View")],
            submitted: [("Cameras", "View")]);

        Assert.Empty(toAdd);
        Assert.Equal([("Playback", "View")], toRemove);
    }
}
