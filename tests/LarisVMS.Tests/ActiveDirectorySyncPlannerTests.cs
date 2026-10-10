using LarisVMS.Infrastructure.ActiveDirectory;

namespace LarisVMS.Tests;

/// <summary>
/// AdSyncPlanner decides what AD sync does to each LarisVMS account: AD owns an AD user's roles
/// entirely, anyone disabled in AD or in no linked group is disabled, and sync only re-enables accounts
/// it disabled itself.
/// </summary>
public class ActiveDirectorySyncPlannerTests
{
    private const string Operators = "S-1-5-21-1-2-3-1101";
    private const string Admins = "S-1-5-21-1-2-3-1102";
    private const string OperatorRole = "role-operator";
    private const string AdminRole = "role-admin";

    private static readonly AdLink[] Links = [new(Operators, OperatorRole), new(Admins, AdminRole)];

    private static AdUserEntry Entry(string sid, string sam = "jdoe", bool active = true, params string[] groups) =>
        new(sid, sam, $"{sam}@corp.example.com", "Jane Doe", active, new HashSet<string>(groups));

    private static AdLinkedUserState Existing(string sid, string sam = "jdoe", bool enabled = true, bool bySync = false,
        params string[] roles) =>
        new("user-" + sid, sid, sam, $"{sam}@corp.example.com", "Jane Doe", enabled, bySync, new HashSet<string>(roles));

    [Fact]
    public void NewMemberOfALinkedGroupIsCreatedWithThatGroupsRole()
    {
        var actions = AdSyncPlanner.Plan([Entry("S-1", groups: Operators)], [], Links, fullSync: true);

        var action = Assert.Single(actions);
        Assert.Equal(AdAccountChange.Create, action.Change);
        Assert.Null(action.UserId);
        Assert.Equal([OperatorRole], action.TargetRoleIds);
    }

    [Fact]
    public void MemberOfSeveralLinkedGroupsGetsEveryLinkedRole()
    {
        var actions = AdSyncPlanner.Plan([Entry("S-1", groups: [Operators, Admins])], [], Links, fullSync: true);

        Assert.Equal(new HashSet<string> { OperatorRole, AdminRole }, Assert.Single(actions).TargetRoleIds);
    }

    [Fact]
    public void GroupLinkedToTwoRolesGrantsBoth()
    {
        AdLink[] links = [new(Operators, OperatorRole), new(Operators, AdminRole)];
        var actions = AdSyncPlanner.Plan([Entry("S-1", groups: Operators)], [], links, fullSync: true);

        Assert.Equal(new HashSet<string> { OperatorRole, AdminRole }, Assert.Single(actions).TargetRoleIds);
    }

    [Fact]
    public void DisabledAdUserWithNoAccountYetIsNotCreated()
    {
        var actions = AdSyncPlanner.Plan([Entry("S-1", active: false, groups: Operators)], [], Links, fullSync: true);

        Assert.Empty(actions);
    }

    [Fact]
    public void ExistingUserDisabledInAdIsDisabled()
    {
        var actions = AdSyncPlanner.Plan(
            [Entry("S-1", active: false, groups: Operators)],
            [Existing("S-1", roles: OperatorRole)], Links, fullSync: true);

        var action = Assert.Single(actions);
        Assert.Equal(AdAccountChange.Disable, action.Change);
        Assert.Equal(AdSyncPlanner.ReasonAdDisabled, action.Reason);
    }

    [Fact]
    public void ExistingUserMissingFromEveryLinkedGroupIsDisabledAndLosesRoles()
    {
        var actions = AdSyncPlanner.Plan([], [Existing("S-1", roles: OperatorRole)], Links, fullSync: true);

        var action = Assert.Single(actions);
        Assert.Equal(AdAccountChange.Disable, action.Change);
        Assert.Empty(action.TargetRoleIds);
        Assert.Equal(AdSyncPlanner.ReasonNoGroups, action.Reason);
    }

    [Fact]
    public void SignInSyncNeverDisablesOtherAccounts()
    {
        var actions = AdSyncPlanner.Plan([Entry("S-1", groups: Operators)],
            [Existing("S-1", roles: OperatorRole), Existing("S-2", sam: "other", roles: OperatorRole)], Links, fullSync: false);

        Assert.Equal("S-1", Assert.Single(actions).Sid);
    }

    [Fact]
    public void AlreadyDisabledAccountMissingFromAdIsLeftDisabledButStillLosesRoles()
    {
        var actions = AdSyncPlanner.Plan([], [Existing("S-1", enabled: false, roles: OperatorRole)], Links, fullSync: true);

        var action = Assert.Single(actions);
        Assert.Equal(AdAccountChange.None, action.Change);
        Assert.Empty(action.TargetRoleIds);
    }

    [Fact]
    public void AccountDisabledBySyncIsReEnabledWhenEligibleAgain()
    {
        var actions = AdSyncPlanner.Plan([Entry("S-1", groups: Operators)],
            [Existing("S-1", enabled: false, bySync: true)], Links, fullSync: true);

        Assert.Equal(AdAccountChange.Enable, Assert.Single(actions).Change);
    }

    [Fact]
    public void ManuallyDisabledAccountStaysDisabledEvenWhenEligible()
    {
        var actions = AdSyncPlanner.Plan([Entry("S-1", groups: Operators)],
            [Existing("S-1", enabled: false, bySync: false)], Links, fullSync: true);

        var action = Assert.Single(actions);
        Assert.Equal(AdAccountChange.None, action.Change);
        Assert.Equal([OperatorRole], action.TargetRoleIds);
    }

    [Fact]
    public void RenamedAdAccountIsMatchedBySidAndFlaggedForUpdate()
    {
        var actions = AdSyncPlanner.Plan([Entry("S-1", sam: "jsmith", groups: Operators)],
            [Existing("S-1", sam: "jdoe", roles: OperatorRole)], Links, fullSync: true);

        var action = Assert.Single(actions);
        Assert.Equal("user-S-1", action.UserId);
        Assert.True(action.IdentityChanged);
        Assert.Equal(AdAccountChange.None, action.Change);
    }

    [Fact]
    public void UnchangedAccountNeedsNoIdentityUpdate()
    {
        var actions = AdSyncPlanner.Plan([Entry("S-1", groups: Operators)],
            [Existing("S-1", roles: OperatorRole)], Links, fullSync: true);

        Assert.False(Assert.Single(actions).IdentityChanged);
    }

    [Fact]
    public void RolesFollowGroupMembershipExactly()
    {
        // Moved from Admins to Operators in AD: loses Admin, gains Operator.
        var actions = AdSyncPlanner.Plan([Entry("S-1", groups: Operators)],
            [Existing("S-1", roles: AdminRole)], Links, fullSync: true);

        Assert.Equal([OperatorRole], Assert.Single(actions).TargetRoleIds);
    }

    [Fact]
    public void SidMatchingIgnoresCase()
    {
        var actions = AdSyncPlanner.Plan([Entry("s-1-5-21-9", groups: Operators.ToLowerInvariant())],
            [Existing("S-1-5-21-9", roles: OperatorRole)], Links, fullSync: true);

        var action = Assert.Single(actions);
        Assert.Equal(AdAccountChange.None, action.Change);
        Assert.Equal([OperatorRole], action.TargetRoleIds);
    }

    [Fact]
    public void NoLinksAtAllDisablesEveryLinkedAccount()
    {
        var actions = AdSyncPlanner.Plan([], [Existing("S-1"), Existing("S-2", sam: "b")], [], fullSync: true);

        Assert.All(actions, a => Assert.Equal(AdAccountChange.Disable, a.Change));
        Assert.Equal(2, actions.Count);
    }
}
