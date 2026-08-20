namespace LarisVMS.Web.Services;

/// <summary>
/// Pure decision logic behind Admin → Settings → Users' two lockout guard rails. Without these, an
/// admin could remove their own (or the last remaining) Administrator role, or disable the last
/// enabled Administrator account, and there is no recovery path in this app short of editing the
/// database directly — no "forgot my role" flow, no break-glass account.
/// </summary>
public static class UserManagementPolicy
{
    /// <summary>True when a save would take the edited user from Administrator to not, and no other
    /// enabled account would still hold it afterward.</summary>
    public static bool WouldRemoveLastAdministrator(bool wasAdministrator, bool staysAdministrator, int otherEnabledAdministratorCount) =>
        wasAdministrator && !staysAdministrator && otherEnabledAdministratorCount == 0;

    /// <summary>True when disabling this account would leave zero other enabled Administrators.</summary>
    public static bool WouldDisableLastAdministrator(bool isAdministrator, int otherEnabledAdministratorCount) =>
        isAdministrator && otherEnabledAdministratorCount == 0;
}
