using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Auth;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>
/// GetAccessibleCameraIdsAsync end to end — CameraAccessServiceTests covers Resolve's pure logic
/// once rows are already fetched; this covers the parts around it (Administrator short-circuit,
/// resolving role-name claims to real role Ids, and the zero-rows-means-unrestricted default that
/// keeps every existing deployment's every existing user seeing everything unless an admin
/// deliberately adds a grant).
/// </summary>
public class CameraAccessServiceIntegrationTests
{
    private static (ApplicationDbContext Db, RoleManager<IdentityRole> Roles, CameraAccessService Service) NewHarness()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new ApplicationDbContext(options);
        var roleStore = new RoleStore<IdentityRole, ApplicationDbContext, string>(db);
        var roleManager = new RoleManager<IdentityRole>(roleStore, [], new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(), Microsoft.Extensions.Logging.Abstractions.NullLogger<RoleManager<IdentityRole>>.Instance);
        return (db, roleManager, new CameraAccessService(db));
    }

    private static ClaimsPrincipal AuthenticatedAs(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    [Fact]
    public async Task ASuperAdminIsAlwaysUnrestrictedEvenWithGrantsPresentForOtherRoles()
    {
        var (db, roles, service) = NewHarness();
        var viewer = await roles.CreateAsync(new IdentityRole("Viewer")) is { Succeeded: true }
            ? await roles.FindByNameAsync("Viewer") : null;
        db.CameraAccesses.Add(new CameraAccess
        {
            Id = Guid.NewGuid(), PrincipalType = CameraAccessPrincipalType.Role, PrincipalId = viewer!.Id,
            ScopeType = CameraAccessScopeType.Camera, ScopeId = Guid.NewGuid(), Actions = CameraAccessActions.View
        });
        var superAdmin = await roles.CreateAsync(new IdentityRole("Super Admin")) is { Succeeded: true }
            ? await roles.FindByNameAsync("Super Admin") : null;
        db.RoleProfiles.Add(new RoleProfile { RoleId = superAdmin!.Id, Tag = RoleTags.SuperAdmin });
        await db.SaveChangesAsync();

        var result = await service.GetAccessibleCameraIdsAsync(AuthenticatedAs("admin-1", "Super Admin"), CameraAccessActions.View);

        Assert.Null(result);
    }

    [Fact]
    public async Task APrincipalWithNoGrantsAtAllIsUnrestricted()
    {
        var (db, roles, service) = NewHarness();
        await roles.CreateAsync(new IdentityRole("Viewer"));

        var result = await service.GetAccessibleCameraIdsAsync(AuthenticatedAs("user-1", "Viewer"), CameraAccessActions.View);

        Assert.Null(result);
    }

    [Fact]
    public async Task ARoleWithAGrantIsNarrowedToExactlyWhatItGrants()
    {
        var (db, roles, service) = NewHarness();
        await roles.CreateAsync(new IdentityRole("Viewer"));
        var viewer = await roles.FindByNameAsync("Viewer");
        var camId = Guid.NewGuid();
        db.Cameras.Add(new Camera { Id = camId, Name = "Front Door", Host = "10.0.0.1", DeviceServiceUri = "http://10.0.0.1/onvif" });
        db.CameraAccesses.Add(new CameraAccess
        {
            Id = Guid.NewGuid(), PrincipalType = CameraAccessPrincipalType.Role, PrincipalId = viewer!.Id,
            ScopeType = CameraAccessScopeType.Camera, ScopeId = camId, Actions = CameraAccessActions.View
        });
        await db.SaveChangesAsync();

        var result = await service.GetAccessibleCameraIdsAsync(AuthenticatedAs("user-1", "Viewer"), CameraAccessActions.View);

        Assert.Equal([camId], result);
    }

    [Fact]
    public async Task AGrantOnOneRoleDoesNotLeakToAUserWithoutThatRole()
    {
        var (db, roles, service) = NewHarness();
        await roles.CreateAsync(new IdentityRole("Viewer"));
        var viewer = await roles.FindByNameAsync("Viewer");
        var camId = Guid.NewGuid();
        db.CameraAccesses.Add(new CameraAccess
        {
            Id = Guid.NewGuid(), PrincipalType = CameraAccessPrincipalType.Role, PrincipalId = viewer!.Id,
            ScopeType = CameraAccessScopeType.Camera, ScopeId = camId, Actions = CameraAccessActions.View
        });
        await db.SaveChangesAsync();

        // "Operator" has no grants of its own — unrestricted, per the zero-rows default — but the
        // real thing under test is that Viewer's specific camera grant plays no part in that answer.
        var result = await service.GetAccessibleCameraIdsAsync(AuthenticatedAs("user-2", "Operator"), CameraAccessActions.View);

        Assert.Null(result);
    }

    [Fact]
    public async Task AnUnauthenticatedPrincipalSeesNothing()
    {
        var (_, _, service) = NewHarness();
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await service.GetAccessibleCameraIdsAsync(anonymous, CameraAccessActions.View);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    // M20: an API-key-authenticated principal carries a Role claim but deliberately no
    // ClaimTypes.NameIdentifier at all (see ApiKeyAuthMiddleware) — this was the actual bug found while
    // building that feature: the old guard treated "no NameIdentifier" as "not authenticated, sees
    // nothing", which silently made every API key see zero cameras regardless of its role's grants.
    [Fact]
    public async Task ARoleOnlyPrincipalWithNoUserIdStillResolvesItsRoleGrant()
    {
        var (db, roles, service) = NewHarness();
        await roles.CreateAsync(new IdentityRole("API/Integration"));
        var apiRole = await roles.FindByNameAsync("API/Integration");
        var camId = Guid.NewGuid();
        db.Cameras.Add(new Camera { Id = camId, Name = "Front Door", Host = "10.0.0.1", DeviceServiceUri = "http://10.0.0.1/onvif" });
        db.CameraAccesses.Add(new CameraAccess
        {
            Id = Guid.NewGuid(), PrincipalType = CameraAccessPrincipalType.Role, PrincipalId = apiRole!.Id,
            ScopeType = CameraAccessScopeType.Camera, ScopeId = camId, Actions = CameraAccessActions.View
        });
        await db.SaveChangesAsync();

        var roleOnly = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "API/Integration")], authenticationType: "ApiKey"));

        var result = await service.GetAccessibleCameraIdsAsync(roleOnly, CameraAccessActions.View);

        Assert.Equal([camId], result);
    }
}
