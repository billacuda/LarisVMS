using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

public class RoleManagementPolicyTests
{
    [Fact]
    public void AdministratorCannotBeRenamed()
    {
        Assert.False(RoleManagementPolicy.CanRename("Administrator"));
    }

    [Fact]
    public void AnyOtherRoleCanBeRenamed()
    {
        Assert.True(RoleManagementPolicy.CanRename("Viewer"));
    }

    [Fact]
    public void AdministratorCanNeverBeDeletedEvenWithNoUsers()
    {
        Assert.False(RoleManagementPolicy.CanDelete("Administrator", userCount: 0));
    }

    [Fact]
    public void ARoleStillHoldingUsersCannotBeDeleted()
    {
        Assert.False(RoleManagementPolicy.CanDelete("Operator", userCount: 3));
    }

    [Fact]
    public void ARoleWithNoUsersCanBeDeleted()
    {
        Assert.True(RoleManagementPolicy.CanDelete("Operator", userCount: 0));
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
