namespace LarisVMS.Core.Auth;

/// <summary>
/// The fixed list of every Resource×Action pair this app actually checks somewhere (an
/// <c>[Authorize("Resource.Action")]</c>, a <c>RequireAuthorization("Resource.Action")</c>, or a
/// <c>PermissionSet.Has("Resource", "Action")</c> nav-visibility check) plus "Settings.View", which
/// SetupService has seeded onto the Viewer role since M1 but nothing yet gates on — kept in the
/// catalog so the permissions editor can show and grant it rather than silently hiding a permission
/// that already exists in the database. <see cref="PermissionPolicyProvider"/> itself accepts *any*
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
    ];
}
