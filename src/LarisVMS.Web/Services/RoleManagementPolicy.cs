namespace LarisVMS.Web.Services;

/// <summary>
/// Pure decision logic behind Admin → Settings → Roles — extracted so the guard rails (what can't be
/// renamed, what can't be deleted, how a submitted checkbox grid reconciles against existing rows) are
/// unit-testable without a real RoleManager/DbContext.
/// </summary>
public static class RoleManagementPolicy
{
    public const string ProtectedRoleName = "Administrator";

    /// <summary>PermissionService.HasPermissionAsync/GetGrantedAsync both bypass the Permission table
    /// entirely by checking for the literal role name "Administrator" (see that class's own doc
    /// comment on why: the role that grants the matrix can't depend on rows in the matrix itself).
    /// Renaming it would silently strip every admin of that bypass with nothing left to grant it
    /// back with, so the name itself is protected, not just the role's Id.</summary>
    public static bool CanRename(string? roleName) =>
        !string.Equals(roleName, ProtectedRoleName, StringComparison.Ordinal);

    /// <summary>Deleting Administrator is never allowed (same reasoning as CanRename). Any other role
    /// can be deleted once nobody still holds it — deleting out from under assigned users would leave
    /// them with a dangling AspNetUserRoles row and every permission check silently failing.</summary>
    public static bool CanDelete(string? roleName, int userCount) =>
        !string.Equals(roleName, ProtectedRoleName, StringComparison.Ordinal) && userCount == 0;

    /// <summary>Reconciles a role's existing Permission rows against a freshly submitted checkbox
    /// grid: whatever's checked but not yet a row needs adding, whatever's a row but no longer checked
    /// needs removing. A pair present in both is left untouched — its Permission.Id and
    /// IsSystemPermission flag survive the save rather than being deleted and re-added.</summary>
    public static (List<(string Resource, string Action)> ToAdd, List<(string Resource, string Action)> ToRemove) DiffPermissions(
        IEnumerable<(string Resource, string Action)> existing,
        IEnumerable<(string Resource, string Action)> submitted)
    {
        var existingSet = existing.ToHashSet();
        var submittedSet = submitted.ToHashSet();
        return (submittedSet.Except(existingSet).ToList(), existingSet.Except(submittedSet).ToList());
    }
}
