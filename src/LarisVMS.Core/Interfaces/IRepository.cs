using System.Security.Claims;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;

namespace LarisVMS.Core.Interfaces;

public interface IRepository<T> where T : class
{
    Task<T?> GetByIdAsync(object id, CancellationToken ct = default);
    Task<List<T>> GetAllAsync(CancellationToken ct = default);
    Task AddAsync(T entity, CancellationToken ct = default);
    void Update(T entity);
    void Remove(T entity);
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

public interface IAuditService
{
    Task LogAsync(string action, string? userId, string? userName, string? ipAddress,
        string? details = null, CancellationToken ct = default);
}

public interface IPermissionService
{
    Task<bool> HasPermissionAsync(string userId, string resource, string action, CancellationToken ct = default);

    /// <summary>Same resolution as <see cref="HasPermissionAsync"/> (role → Permission rows, with the
    /// same Super Admin bypass), but starting from a role name instead of a user id — for a principal
    /// that carries no <c>ClaimTypes.NameIdentifier</c> at all, namely an API-key-authenticated request
    /// (M20). An API key is bound to exactly one Role and nothing else, so there's no user to look
    /// up.</summary>
    Task<bool> HasPermissionForRoleNameAsync(string roleName, string resource, string action, CancellationToken ct = default);

    /// <summary>Every (Resource, Action) pair the given principal's roles grant, resolved in at most
    /// one database round trip regardless of how many pairs the caller ends up checking — built for
    /// _Layout.cshtml's nav, which needs several yes/no answers on every single page render and can't
    /// afford <see cref="HasPermissionAsync"/>'s own per-call cost (role lookup + permission lookup)
    /// multiplied by the number of nav buttons. Role names are read straight off the principal's own
    /// claims rather than re-queried from the database — see <see cref="PermissionSet"/>'s own doc
    /// comment for why that's safe.</summary>
    Task<PermissionSet> GetGrantedAsync(ClaimsPrincipal user, CancellationToken ct = default);
}

/// <summary>The result of <see cref="IPermissionService.GetGrantedAsync"/> — an Administrator-aware
/// membership test over a fixed snapshot of granted (Resource, Action) pairs, rather than a live
/// per-call query. <see cref="IsAdministrator"/> is checked first in <see cref="Has"/>, matching
/// PermissionService.HasPermissionAsync's own short-circuit, so this can never disagree with the
/// per-call check about whether an Administrator has a given permission.</summary>
public sealed class PermissionSet(bool isAdministrator, IReadOnlySet<(string Resource, string Action)> granted)
{
    public bool IsAdministrator { get; } = isAdministrator;

    public bool Has(string resource, string action) => IsAdministrator || granted.Contains((resource, action));

    public static readonly PermissionSet None = new(false, new HashSet<(string, string)>());
}

/// <summary>
/// Seeds the roles/permissions overhaul's role metadata: renames Administrator/Viewer in place to
/// Super Admin/Guest-Viewer (preserving their Id, so every existing Permission/CameraAccess/
/// AspNetUserRoles row keeps resolving with no remapping), upserts their RoleProfile rows, and
/// creates the other 6 built-in roles plus their RoleProfile rows. Idempotent — safe to call on
/// every app startup; each step checks its own precondition (role exists under old/new name,
/// RoleProfile row already present) rather than relying on a single all-or-nothing guard, so a
/// partially-applied prior run or a retried deploy never duplicates rows or re-does finished work.
/// Never touches a pre-existing custom role — no rename, no RoleProfile row, no permission changes.
/// </summary>
public interface IRoleSeedService
{
    Task SeedAsync(CancellationToken ct = default);
}

/// <summary>
/// Seeds the built-in "All Cameras" CameraGroup (CameraGroup.AllCamerasId) if it doesn't exist yet,
/// then backfills any camera not already linked to it. Idempotent — safe on every startup, same
/// shape as IRoleSeedService: a camera added between startups is picked up by CameraService.AddAsync
/// directly, so this only ever needs to catch up rows from before the group existed or from a report
/// that never landed.
/// </summary>
public interface ICameraGroupSeedService
{
    Task SeedAsync(CancellationToken ct = default);
}

public interface ISetupService
{
    Task<bool> IsSetupCompleteAsync(CancellationToken ct = default);
    Task<bool> IsDatabaseConfiguredAsync(CancellationToken ct = default);
    Task<bool> IsAdminCreatedAsync(CancellationToken ct = default);

    Task SetupDatabaseAsync(string serverName, string databaseName, string? username, string? password,
        CancellationToken ct = default);

    Task CompleteSetupAsync(string adminEmail, string adminPassword, CancellationToken ct = default);

    Task SaveBrandingAsync(string appName, string primaryColor, CancellationToken ct = default);

    /// <summary>Generates (or returns the existing) node registration key so the wizard can print
    /// the install-node.ps1 command line. The Nodes table itself lands in M3.</summary>
    Task<string> GetOrCreateNodeRegistrationKeyAsync(CancellationToken ct = default);

    Task FinalizeSetupAsync(CancellationToken ct = default);

    Task<(string? FullPath, string? ContentType)> GetLogoInfoAsync();
}

/// <summary>
/// Walks Camera → Node → Global → compiled-in default, memory-cached with invalidation on write.
/// Every feature that is "global with per-camera override" (retention, recording mode, motion
/// sensitivity, ...) reads through this rather than querying Settings/SettingOverride directly.
/// <c>nodeId</c> is optional on every call — if omitted but <c>cameraId</c> is given, the camera's
/// current Node is looked up so a camera-level default still falls through its node's override
/// before Global. CameraGroup-scoped overrides (<see cref="SettingScope.CameraGroup"/>) are modeled
/// in the schema and the enum but there's no UI yet to set one and no ancestor-walk implemented —
/// deferred until a feature actually needs it, per CHANGELOG / plan.
/// </summary>
public interface ISettingsResolver
{
    Task<string?> GetRawAsync(string key, Guid? cameraId = null, Guid? nodeId = null, CancellationToken ct = default);
    Task<T> GetAsync<T>(string key, T defaultValue, Guid? cameraId = null, Guid? nodeId = null, CancellationToken ct = default);

    /// <summary>Which scope actually supplied the resolved value — Camera/Node/Global — or null if
    /// nothing is set anywhere and the caller's compiled-in default applies. Drives "inherited" vs
    /// "override" badges in the UI without duplicating the resolution walk.</summary>
    Task<SettingScope?> GetSourceAsync(string key, Guid? cameraId = null, Guid? nodeId = null, CancellationToken ct = default);

    /// <summary>The raw value of exactly this scope's own override row, with no walk — null if this
    /// scope has no override of its own (regardless of what Global or a narrower scope resolves to).
    /// For populating an edit form's override field, distinct from GetRawAsync's resolved/effective
    /// value used everywhere else.</summary>
    Task<string?> GetOwnOverrideAsync(SettingScope scope, Guid scopeId, string key, CancellationToken ct = default);

    Task SetGlobalAsync(string key, string value, string? modifiedBy = null, CancellationToken ct = default);

    /// <summary>Sets or clears a Camera/Node/CameraGroup-scoped override. A null or blank value
    /// removes the override row, reverting that scope to whatever the next scope down resolves to.</summary>
    Task SetOverrideAsync(SettingScope scope, Guid scopeId, string key, string? value, string? modifiedBy = null, CancellationToken ct = default);

    Task InvalidateAsync();
}

/// <summary>
/// Per-user client preferences — the server-backed replacement for what used to live only in
/// <c>localStorage</c> (theme, last-watched view, table page size, playback clock format, and so
/// on). Deliberately a flat key/value bag rather than typed columns, matching <see cref="Setting"/>'s
/// own shape: new preferences are added on the client without a schema change on this side.
/// </summary>
public interface IUserPreferenceService
{
    /// <summary>Every preference this user has ever set, as a flat key/value map — the client
    /// fetches this once on page load rather than one request per key.</summary>
    Task<Dictionary<string, string>> GetAllAsync(string userId, CancellationToken ct = default);

    Task SetAsync(string userId, string key, string value, CancellationToken ct = default);
}

/// <summary>
/// Resolves <see cref="CameraAccess"/> — the per-camera ACL layered on top of the global
/// Resource×Action RBAC (M1's design; unenforced anywhere until M14). See
/// <see cref="CameraAccessScopeType"/> for how a row scopes to every camera, one
/// <see cref="CameraGroup"/> (and, cascading, its descendants), or one camera.
/// </summary>
public interface ICameraAccessService
{
    /// <summary>Every camera id this principal can exercise <paramref name="action"/> on, or
    /// <c>null</c> meaning unrestricted (every camera). Null covers three cases deliberately treated
    /// the same way: an Administrator (matches global RBAC's own implicit-everything rule), a
    /// principal holding at least one <c>ScopeType.All</c> grant for this action, and — the case
    /// that matters most for not silently breaking every existing deployment — a principal with
    /// <i>zero</i> CameraAccess rows of their own. This table narrows access; it was never meant to
    /// default-deny the moment it exists unpopulated, and until this pass nothing ever wrote a row
    /// into it, so every current user of every current deployment has exactly zero rows today.</summary>
    Task<HashSet<Guid>?> GetAccessibleCameraIdsAsync(ClaimsPrincipal user,
        CameraAccessActions action, CancellationToken ct = default);
}
