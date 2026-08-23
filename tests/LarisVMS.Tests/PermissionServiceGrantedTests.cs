using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Auth;
using LarisVMS.Core.Entities;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;

namespace LarisVMS.Tests;

/// <summary>
/// PermissionService.GetGrantedAsync backs every permission-gated nav button in _Layout.cshtml — a
/// wrong answer here means a button either disappears for someone who could use it, or stays visible
/// as a dead link for someone who can't. Deliberately exercises only the ClaimsPrincipal + DbContext
/// path (never HasPermissionAsync, which needs a real UserManager) — GetGrantedAsync never touches
/// the UserManager constructor argument at all, which is exactly why it can answer several yes/no
/// questions from one role-claims read plus at most one query, instead of HasPermissionAsync's own
/// per-call user/role lookup.
/// </summary>
public class PermissionServiceGrantedTests
{
    private static ApplicationDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static PermissionService NewService(ApplicationDbContext db) => new(db, null!);

    private static ClaimsPrincipal AuthenticatedAs(params string[] roles)
    {
        var claims = roles.Select(r => new Claim(ClaimTypes.Role, r));
        // The authenticationType argument is what makes Identity.IsAuthenticated true — a
        // ClaimsIdentity built with none is, by design, always unauthenticated regardless of claims.
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    [Fact]
    public async Task AnAnonymousPrincipalGetsNothing()
    {
        using var db = NewDb();
        var result = await NewService(db).GetGrantedAsync(Anonymous());

        Assert.False(result.IsAdministrator);
        Assert.False(result.Has("Cameras", "View"));
    }

    [Fact]
    public async Task AnAuthenticatedUserWithNoRolesGetsNothing()
    {
        using var db = NewDb();
        var result = await NewService(db).GetGrantedAsync(AuthenticatedAs());

        Assert.False(result.Has("Cameras", "View"));
    }

    [Fact]
    public async Task SuperAdminHasEverythingWithNoPermissionRowsSeededAtAll()
    {
        // Proves the short-circuit: a role tagged SUPER is enough, with zero Permission rows in the
        // database — matches HasPermissionAsync's own reasoning that Super Admin can't depend on rows
        // in the matrix it's the one granting. The bypass is resolved via RoleProfile.Tag, not the
        // role's (renamable) Name, so both a real role row and its RoleProfile need to exist here.
        using var db = NewDb();
        var role = new IdentityRole("Super Admin") { NormalizedName = "SUPER ADMIN" };
        db.Roles.Add(role);
        db.RoleProfiles.Add(new RoleProfile { RoleId = role.Id, Tag = RoleTags.SuperAdmin });
        await db.SaveChangesAsync();

        var result = await NewService(db).GetGrantedAsync(AuthenticatedAs("Super Admin"));

        Assert.True(result.IsAdministrator);
        Assert.True(result.Has("AnythingAtAll", "EvenMadeUp"));
    }

    [Fact]
    public async Task ANonAdminRoleOnlyGrantsItsOwnSeededPermissions()
    {
        using var db = NewDb();
        var role = new IdentityRole("Viewer") { NormalizedName = "VIEWER" };
        db.Roles.Add(role);
        db.Permissions.Add(new Permission { Id = Guid.NewGuid(), RoleId = role.Id, Resource = "Cameras", Action = "View" });
        await db.SaveChangesAsync();

        var result = await NewService(db).GetGrantedAsync(AuthenticatedAs("Viewer"));

        Assert.False(result.IsAdministrator);
        Assert.True(result.Has("Cameras", "View"));
        Assert.False(result.Has("Nodes", "Edit"));
    }

    [Fact]
    public async Task PermissionsFromEveryRoleTheUserHoldsAreUnioned()
    {
        using var db = NewDb();
        var viewer = new IdentityRole("Viewer") { NormalizedName = "VIEWER" };
        var operatorRole = new IdentityRole("Operator") { NormalizedName = "OPERATOR" };
        db.Roles.AddRange(viewer, operatorRole);
        db.Permissions.AddRange(
            new Permission { Id = Guid.NewGuid(), RoleId = viewer.Id, Resource = "Cameras", Action = "View" },
            new Permission { Id = Guid.NewGuid(), RoleId = operatorRole.Id, Resource = "Playback", Action = "View" });
        await db.SaveChangesAsync();

        var result = await NewService(db).GetGrantedAsync(AuthenticatedAs("Viewer", "Operator"));

        Assert.True(result.Has("Cameras", "View"));
        Assert.True(result.Has("Playback", "View"));
        Assert.False(result.Has("Exports", "View"));
    }
}
