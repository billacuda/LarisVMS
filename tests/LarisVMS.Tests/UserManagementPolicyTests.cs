using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

public class UserManagementPolicyTests
{
    [Fact]
    public void RemovingAdministratorFromTheOnlyAdministratorIsBlocked()
    {
        Assert.True(UserManagementPolicy.WouldRemoveLastAdministrator(
            wasAdministrator: true, staysAdministrator: false, otherEnabledAdministratorCount: 0));
    }

    [Fact]
    public void RemovingAdministratorIsFineWhenAnotherEnabledOneRemains()
    {
        Assert.False(UserManagementPolicy.WouldRemoveLastAdministrator(
            wasAdministrator: true, staysAdministrator: false, otherEnabledAdministratorCount: 1));
    }

    [Fact]
    public void KeepingAdministratorNeverTripsTheGuard()
    {
        Assert.False(UserManagementPolicy.WouldRemoveLastAdministrator(
            wasAdministrator: true, staysAdministrator: true, otherEnabledAdministratorCount: 0));
    }

    [Fact]
    public void ANonAdministratorEditNeverTripsTheGuard()
    {
        Assert.False(UserManagementPolicy.WouldRemoveLastAdministrator(
            wasAdministrator: false, staysAdministrator: false, otherEnabledAdministratorCount: 0));
    }

    [Fact]
    public void DisablingTheLastEnabledAdministratorIsBlocked()
    {
        Assert.True(UserManagementPolicy.WouldDisableLastAdministrator(
            isAdministrator: true, otherEnabledAdministratorCount: 0));
    }

    [Fact]
    public void DisablingAnAdministratorIsFineWhenAnotherEnabledOneRemains()
    {
        Assert.False(UserManagementPolicy.WouldDisableLastAdministrator(
            isAdministrator: true, otherEnabledAdministratorCount: 1));
    }

    [Fact]
    public void DisablingANonAdministratorNeverTripsTheGuard()
    {
        Assert.False(UserManagementPolicy.WouldDisableLastAdministrator(
            isAdministrator: false, otherEnabledAdministratorCount: 0));
    }
}
