using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LarisVMS.Core.Auth;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

/// <inheritdoc cref="IRoleSeedService" />
public class RoleSeedService(
    ApplicationDbContext db,
    RoleManager<IdentityRole> roleManager,
    UserManager<ApplicationUser> userManager,
    ILogger<RoleSeedService> logger) : IRoleSeedService
{
    private sealed record BuiltInRole(string Tag, string Name, RoleScopeTier MaxScopeTier, bool AutoExpires,
        int? DefaultExpiryMinutes, int? PtzPriorityLevel, int? PtzLockoutSeconds);

    private static readonly BuiltInRole[] BuiltIns =
    [
        new(RoleTags.SuperAdmin, "Super Admin", RoleScopeTier.Org, false, null, 3000, 900),
        new(RoleTags.SystemAdmin, "System Admin", RoleScopeTier.Site, false, null, 2500, 900),
        new(RoleTags.SecurityManager, "Security Manager", RoleScopeTier.Site, false, null, 2000, 900),
        new(RoleTags.Operator, "Operator", RoleScopeTier.Group, true, 480, 1500, 900),
        new(RoleTags.Auditor, "Investigator/Auditor", RoleScopeTier.Site, false, null, null, null),
        new(RoleTags.Technician, "Installer/Technician", RoleScopeTier.Group, true, 43200, 1000, 900),
        new(RoleTags.GuestViewer, "Guest/Viewer", RoleScopeTier.Group, true, 1440, null, null),
        new(RoleTags.ApiIntegration, "API/Integration", RoleScopeTier.Org, false, null, null, null),
    ];

    // Pre-overhaul role names that rename in place to a built-in's new name, keyed by target Tag.
    // Only SUPER and VIEWER pre-date this overhaul (Administrator, Viewer) — the other 6 built-ins
    // are always net-new creations, never renames.
    private static readonly Dictionary<string, string> LegacyNames = new()
    {
        [RoleTags.SuperAdmin] = "Administrator",
        [RoleTags.GuestViewer] = "Viewer",
    };

    /// <summary>Pass 3 (full permission matrix) seeding for the 6 net-new built-ins — SUPER bypasses
    /// the Permission table entirely (see PermissionService) and VIEWER's own rows predate this
    /// overhaul (SetupService.SeedViewerPermissionsAsync), so neither appears here.
    ///
    /// Every row here maps one permission_matrix.txt cell onto a real enforcement point this app
    /// has — full-tier cells at face value, but a "limited" cell collapses to the same grant as
    /// "full" (never to "none"): this app's default is "a CameraAccess/Permission row = unrestricted
    /// until an admin narrows it," so there is nothing to seed a *narrower* default into without an
    /// admin's real site/group structure to point it at (no CameraGroup exists yet on a fresh
    /// install). Narrowing a "limited" role down for a real deployment (e.g. Security Manager to
    /// just their own site) is a deliberate post-seed admin action via Camera Access, exactly like
    /// every pre-existing role already works — see CameraAccess.cshtml's own doc comment. A "none"
    /// cell is simply left out of both lists below.
    ///
    /// Matrix rows with no code home at all are deliberately absent from every role's grant, not
    /// silently defaulted: "Delete recordings" (no manual-delete feature exists — only automatic
    /// retention eviction), "Receive real-time alerts"/"Acknowledge or dismiss alerts" (no in-app
    /// alert inbox exists — only rule-based email/webhook delivery), "Manage billing &amp; licensing"
    /// (no such feature — LarisVMS's licensing model is AGPL + commercial dual-licensing, not
    /// per-feature gating), "Configure integrations / API keys" (IntegrationKey is one field inside
    /// Cameras.Edit's single coarse gate with no independent enforcement point — API/Integration's
    /// need for that one field without general camera-edit rights isn't buildable without a
    /// dedicated integration-key surface this app doesn't have yet), "Cross-site visibility" (already
    /// derived from RoleProfile.MaxScopeTier, pass 2 — not an independently grantable cell). "Assign
    /// cameras to groups/sites" *was* deferred here for the same field-level-split reason as
    /// "Add/remove cameras" and "Network/firmware configuration" below, but shipped as its own
    /// dedicated surface shortly after this pass (ICameraService.SetCameraGroupsAsync, Cameras/Edit's
    /// multi-select, and Cameras/Groups' own per-group camera picker) — CameraGroups.Edit gates the
    /// latter, Cameras.Edit still gates the former (unchanged, still merged, still deferred).
    ///
    /// One deliberate deviation from the literal matrix: Technician gets Cameras.View even though its
    /// own row marks that "none" — Cameras.View is also this app's gate for Cameras/Index (the camera
    /// list, the only real entry point into Cameras/Edit), so a literal "none" would leave Technician
    /// unable to reach any camera it's otherwise fully entitled to configure.
    private sealed record BuiltInGrant(
        (string Resource, string Action)[] Permissions,
        CameraAccessActions CameraActions);

    private static readonly Dictionary<string, BuiltInGrant> Pass3Grants = new()
    {
        [RoleTags.SystemAdmin] = new(
            [("Cameras", "View"), ("Views", "View"), ("Views", "Edit"), ("Playback", "View"),
             ("Exports", "View"), ("Bookmarks", "Edit"), ("Alerts", "Edit"), ("Cameras", "Edit"),
             ("Dashboard", "View"), ("CameraGroups", "Edit"), ("Users", "Edit"), ("Roles", "Assign"),
             ("Logs", "View"), ("Logs", "Export"), ("Retention", "Edit"), ("SystemLogs", "View")],
            CameraAccessActions.View | CameraAccessActions.Playback | CameraAccessActions.Export |
            CameraAccessActions.Ptz | CameraAccessActions.Talk | CameraAccessActions.Configure),

        [RoleTags.SecurityManager] = new(
            [("Cameras", "View"), ("Views", "View"), ("Views", "Edit"), ("Playback", "View"),
             ("Exports", "View"), ("Bookmarks", "Edit"), ("Alerts", "Edit"), ("Cameras", "Edit"),
             ("Dashboard", "View"), ("CameraGroups", "Edit"), ("Users", "Edit"),
             ("Logs", "View"), ("Logs", "Export")],
            CameraAccessActions.View | CameraAccessActions.Playback | CameraAccessActions.Export |
            CameraAccessActions.Ptz | CameraAccessActions.Talk | CameraAccessActions.Configure),

        [RoleTags.Operator] = new(
            [("Cameras", "View"), ("Views", "View"), ("Views", "Edit"), ("Bookmarks", "Edit"),
             ("Dashboard", "View")],
            CameraAccessActions.View | CameraAccessActions.Playback |
            CameraAccessActions.Ptz | CameraAccessActions.Talk),

        [RoleTags.Auditor] = new(
            [("Playback", "View"), ("Exports", "View"), ("Bookmarks", "Edit"),
             ("Logs", "View"), ("Logs", "Export")],
            CameraAccessActions.Playback | CameraAccessActions.Export),

        [RoleTags.Technician] = new(
            [("Cameras", "View"), ("Cameras", "Edit"), ("Dashboard", "View"),
             ("Logs", "View"), ("SystemLogs", "View")],
            CameraAccessActions.Ptz | CameraAccessActions.Configure),

        [RoleTags.ApiIntegration] = new(
            [("Cameras", "View"), ("Playback", "View"), ("Exports", "View"), ("Dashboard", "View")],
            CameraAccessActions.View | CameraAccessActions.Playback | CameraAccessActions.Export),
    };

    public async Task SeedAsync(CancellationToken ct = default)
    {
        foreach (var builtIn in BuiltIns)
        {
            // Already seeded (by a prior run of this method, on this or an earlier startup)? A
            // RoleProfile row for this Tag is the source of truth for that, independent of whatever
            // display name an admin may have since given the role — never touch it again once seeded,
            // so an admin's own edits to a built-in role's scope tier/PTZ/expiry config survive every
            // future app restart instead of being silently reverted by this method re-running.
            var alreadySeeded = await db.RoleProfiles.AnyAsync(p => p.Tag == builtIn.Tag, ct);
            if (alreadySeeded) continue;

            var role = await ResolveOrCreateRoleAsync(builtIn, ct);
            if (role is null) continue; // creation failed (e.g. Identity validation) — leave for the next startup to retry

            db.RoleProfiles.Add(new RoleProfile
            {
                RoleId = role.Id,
                Tag = builtIn.Tag,
                MaxScopeTier = builtIn.MaxScopeTier,
                AutoExpires = builtIn.AutoExpires,
                DefaultExpiryMinutes = builtIn.DefaultExpiryMinutes,
                PtzPriorityLevel = builtIn.PtzPriorityLevel,
                PtzLockoutSeconds = builtIn.PtzLockoutSeconds,
                IsSystemRole = true
            });

            // Same SaveChangesAsync as the RoleProfile row above, not a separate one — if this fails
            // partway, the RoleProfile row (this method's own "already seeded" check) never commits
            // either, so a retried startup redoes this role's grants from scratch instead of leaving
            // it with a RoleProfile but no permissions.
            if (Pass3Grants.TryGetValue(builtIn.Tag, out var grant))
            {
                foreach (var (resource, action) in grant.Permissions)
                    db.Permissions.Add(new Permission
                    {
                        Id = Guid.NewGuid(), RoleId = role.Id, Resource = resource, Action = action, IsSystemPermission = true
                    });

                if (grant.CameraActions != CameraAccessActions.None)
                    db.CameraAccesses.Add(new CameraAccess
                    {
                        Id = Guid.NewGuid(),
                        PrincipalType = CameraAccessPrincipalType.Role,
                        PrincipalId = role.Id,
                        ScopeType = CameraAccessScopeType.All,
                        ScopeId = null,
                        Actions = grant.CameraActions
                    });
            }

            await db.SaveChangesAsync(ct);
        }
    }

    private async Task<IdentityRole?> ResolveOrCreateRoleAsync(BuiltInRole builtIn, CancellationToken ct)
    {
        var isRename = LegacyNames.TryGetValue(builtIn.Tag, out var legacyName);

        if (isRename)
        {
            // A fresh install's SetupService seeds "Super Admin"/"Guest/Viewer" directly under their
            // final names — if the target name already exists, this is that case (or a prior partial
            // run that renamed but failed to save the RoleProfile row before crashing). Either way
            // it's the correct role to attach the profile to. Only SUPER/VIEWER get this treatment:
            // nothing but this method or SetupService has ever named a role "Super Admin" or
            // "Guest/Viewer", so there's no realistic pre-existing-custom-role collision to guard
            // against here the way there is for the 6 net-new roles below.
            var existingByTargetName = await roleManager.FindByNameAsync(builtIn.Name);
            if (existingByTargetName is not null) return existingByTargetName;

            // Rename the pre-overhaul role (Administrator/Viewer) in place via RoleManager (not raw
            // SQL) so Identity's NormalizedName stays consistent — preserves the role's Id, so every
            // existing Permission/CameraAccess/AspNetUserRoles row keeps resolving with zero
            // remapping.
            var legacy = await roleManager.FindByNameAsync(legacyName!);
            if (legacy is null)
            {
                // Neither name exists — shouldn't happen since SetupService always creates one of
                // them, but create fresh defensively rather than leaving this built-in unseeded.
                var freshRole = new IdentityRole(builtIn.Name);
                var freshResult = await roleManager.CreateAsync(freshRole);
                return freshResult.Succeeded ? freshRole : null;
            }

            legacy.Name = builtIn.Name;
            var renameResult = await roleManager.UpdateAsync(legacy);
            if (!renameResult.Succeeded)
            {
                logger.LogWarning("RoleSeedService: renaming {LegacyName} to {NewName} failed: {Errors}",
                    legacyName, builtIn.Name, string.Join(" ", renameResult.Errors.Select(e => e.Description)));
                return null;
            }

            // Safety net alongside the tag-based bypass refactor in PermissionService/
            // CameraAccessService: an already-logged-in holder's auth cookie still carries the old
            // role-name claim until their next login or a SecurityStampValidator refresh. Bumping the
            // stamp forces that refresh on their very next request instead of leaving a live
            // session's bypass silently broken until they happen to re-login.
            var holderIds = await db.UserRoles.Where(ur => ur.RoleId == legacy.Id)
                .Select(ur => ur.UserId).ToListAsync(ct);
            foreach (var holderId in holderIds)
            {
                var holder = await userManager.FindByIdAsync(holderId);
                if (holder is not null) await userManager.UpdateSecurityStampAsync(holder);
            }

            return legacy;
        }

        // One of the 6 net-new built-ins. Unlike SUPER/VIEWER above, a role already holding this
        // exact target name here is necessarily a pre-existing admin-created custom role — nothing
        // else has ever created e.g. "Security Manager" before this method exists. Never silently
        // adopt or overwrite it: seed the built-in under a disambiguated name instead and log a
        // warning so the collision is visible, not silently swallowed.
        var targetName = builtIn.Name;
        if (await roleManager.RoleExistsAsync(targetName))
        {
            targetName = $"{builtIn.Name} (built-in)";
            logger.LogWarning(
                "RoleSeedService: a role named {Name} already exists (not created by this seed) — " +
                "seeding the {Tag} built-in role as {DisambiguatedName} instead so the existing custom " +
                "role is left untouched.", builtIn.Name, builtIn.Tag, targetName);

            if (await roleManager.RoleExistsAsync(targetName))
            {
                logger.LogWarning(
                    "RoleSeedService: {DisambiguatedName} also already exists — skipping the {Tag} " +
                    "built-in role this run; resolve the name collision manually.", targetName, builtIn.Tag);
                return null;
            }
        }

        var role = new IdentityRole(targetName);
        var createResult = await roleManager.CreateAsync(role);
        return createResult.Succeeded ? role : null;
    }
}
