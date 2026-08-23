using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using LarisVMS.Core.Entities;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>
/// Every Permission row SeedViewerPermissionsAsync ever wrote used the literal string "Viewer" (the
/// role's pre-overhaul name) as RoleId instead of the real AspNetRoles.Id GUID — invisible in review
/// because both look like "a role identifier", and invisible in production because the only user
/// this app has ever really exercised is a Super Admin, which bypasses the Permission table via
/// PermissionService's own implicit-everything short-circuit. This doesn't just assert the stored
/// RoleId matches the real role's Id — it runs the actual seed-then-resolve pipeline end to end through
/// PermissionService.GetGrantedAsync, which is the only way this class of bug (a value that "looks
/// like" the right type but is compared against the wrong thing) reliably gets caught.
/// </summary>
public class SetupServiceViewerPermissionSeedingTests
{
    private static (ApplicationDbContext Db, RoleManager<IdentityRole> Roles, SetupService Setup) NewHarness()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new ApplicationDbContext(options);

        var roleStore = new RoleStore<IdentityRole, ApplicationDbContext, string>(db);
        var roleManager = new RoleManager<IdentityRole>(roleStore, [], new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(), NullLogger<RoleManager<IdentityRole>>.Instance);

        // userManager is never touched by SeedViewerPermissionsAsync itself (only by
        // CompleteSetupAsync, for admin-account creation) — null is safe for what this test exercises.
        var setup = new SetupService(db, null!, roleManager, null!, null!);

        return (db, roleManager, setup);
    }

    [Fact]
    public async Task SeededPermissionRowsUseTheRealRoleIdNotTheLiteralRoleName()
    {
        var (db, roles, setup) = NewHarness();
        await roles.CreateAsync(new IdentityRole("Guest/Viewer"));
        var viewerRole = await roles.FindByNameAsync("Guest/Viewer");

        await setup.SeedViewerPermissionsAsync(CancellationToken.None);

        var permissions = await db.Permissions.ToListAsync();
        Assert.NotEmpty(permissions);
        Assert.All(permissions, p => Assert.Equal(viewerRole!.Id, p.RoleId));
        Assert.DoesNotContain(permissions, p => p.RoleId == "Guest/Viewer");
    }

    [Fact]
    public async Task ViewerCanActuallyResolveAPermissionEndToEndAfterSeeding()
    {
        // The real regression test: seed exactly as SetupService does, then ask the exact same
        // question the navbar and every [Authorize("...")] page ask at runtime. Before the fix this
        // returned false for every resource, unconditionally — the RoleId never matched anything.
        var (db, roles, setup) = NewHarness();
        await roles.CreateAsync(new IdentityRole("Guest/Viewer"));
        await setup.SeedViewerPermissionsAsync(CancellationToken.None);

        var permissionService = new PermissionService(db, null!);
        var user = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Guest/Viewer")],
                "TestAuth"));

        var granted = await permissionService.GetGrantedAsync(user);

        Assert.True(granted.Has("Logs", "View"));
        Assert.True(granted.Has("Playback", "View"));
        Assert.False(granted.Has("Nodes", "Edit"));
    }

    [Fact]
    public async Task SeedingIsIdempotentAcrossRepeatedCalls()
    {
        var (db, roles, setup) = NewHarness();
        await roles.CreateAsync(new IdentityRole("Guest/Viewer"));

        await setup.SeedViewerPermissionsAsync(CancellationToken.None);
        var firstCount = await db.Permissions.CountAsync();
        await setup.SeedViewerPermissionsAsync(CancellationToken.None);

        Assert.Equal(firstCount, await db.Permissions.CountAsync());
    }
}
