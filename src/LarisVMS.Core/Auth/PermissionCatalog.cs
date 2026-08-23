namespace LarisVMS.Core.Auth;

/// <summary>
/// The fixed list of every Resource×Action pair this app actually checks somewhere (an
/// <c>[Authorize("Resource.Action")]</c>, a <c>RequireAuthorization("Resource.Action")</c>, or a
/// <c>PermissionSet.Has("Resource", "Action")</c> nav-visibility check), plus two that exist in the
/// database and the permissions editor but nothing gates on yet: "Settings.View" (seeded onto Viewer
/// since M1) and "Dashboard.View" (added in the roles/permissions overhaul's pass 3, matching
/// permission_matrix.txt's "View device health &amp; diagnostics" row, but not wired up to
/// Pages/Index — see that page's own doc comment for why). Both are kept in the catalog so the
/// permissions editor can show and grant them rather than silently hiding a permission that already
/// exists in the database. <see cref="PermissionPolicyProvider"/> itself accepts *any*
/// "{Resource}.{Action}" string dynamically and doesn't consult this list — this exists purely so the
/// admin permissions editor has a closed, known set of checkboxes to render instead of free text,
/// which would let an admin grant a typo'd permission that matches nothing anywhere.
/// </summary>
public static class PermissionCatalog
{
    public readonly record struct Entry(string Resource, string Action, string Label);

    public static readonly IReadOnlyList<Entry> All =
    [
        new("Cameras", "View", "View cameras (live view, camera list)"),
        new("Cameras", "Edit", "Add, edit, and delete cameras"),
        new("Views", "View", "View saved views"),
        new("Views", "Edit", "Create and edit saved views"),
        new("Playback", "View", "Play back recorded footage"),
        new("Exports", "View", "Create and download exports"),
        new("Logs", "View", "View the audit log"),
        new("SystemLogs", "View", "View system/application logs"),
        new("Settings", "View", "View admin settings"),
        new("Settings", "Edit", "Change admin settings"),
        new("Backups", "Edit", "Create, restore, and configure backups"),
        new("Nodes", "Edit", "Manage recording nodes"),
        new("Plugins", "View", "View the plugins page"),
        new("Alerts", "Edit", "Manage alert rules and delivery channels"),

        // Roles/permissions overhaul, pass 3 — added to reach the target permission_matrix.txt's
        // 25-permission shape. Each maps a matrix row onto a genuinely separate enforcement point
        // this app already has (or now has); see RoleSeedService's per-role seeding table and the
        // pass 3 CHANGELOG entry for the full row-by-row mapping, including which matrix rows have
        // no code home yet and were deliberately left unseeded rather than half-built.
        new("Bookmarks", "Edit", "Create bookmarks on recorded footage"),
        new("Dashboard", "View", "View the camera health dashboard"),
        new("CameraGroups", "Edit", "Create, rename, and delete camera groups"),
        new("Users", "Edit", "Create, edit, and deactivate user accounts"),
        new("Roles", "Assign", "Assign roles to user accounts"),
        new("Retention", "Edit", "Change the data retention policy"),
        new("Logs", "Export", "Export the audit log"),
    ];
}
