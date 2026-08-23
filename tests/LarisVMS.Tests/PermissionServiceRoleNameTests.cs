using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Auth;
using LarisVMS.Core.Entities;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>
/// HasPermissionForRoleNameAsync — the M20 API-key resolution path, mirroring HasPermissionAsync's own
/// role → Permission-rows walk and Super Admin bypass, but starting from a role name instead of a user
/// id (an API key has no user, only a Role — see PermissionAuthorizationHandler's role-claim branch).
/// </summary>
public class PermissionServiceRoleNameTests
{
    private static ApplicationDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static PermissionService NewService(ApplicationDbContext db) => new(db, null!);

    [Fact]
    public async Task AnUnknownRoleNameGrantsNothing()
    {
        using var db = NewDb();

        Assert.False(await NewService(db).HasPermissionForRoleNameAsync("Nonexistent", "Cameras", "View"));
    }

    [Fact]
    public async Task ARoleWithTheMatchingSeededPermissionIsGranted()
    {
        using var db = NewDb();
        var role = new IdentityRole("API/Integration") { NormalizedName = "API/INTEGRATION" };
        db.Roles.Add(role);
        db.Permissions.Add(new Permission { Id = Guid.NewGuid(), RoleId = role.Id, Resource = "Dashboard", Action = "View" });
        await db.SaveChangesAsync();

        Assert.True(await NewService(db).HasPermissionForRoleNameAsync("API/Integration", "Dashboard", "View"));
        Assert.False(await NewService(db).HasPermissionForRoleNameAsync("API/Integration", "Nodes", "Edit"));
    }

    [Fact]
    public async Task SuperAdminIsGrantedEverythingWithNoPermissionRowsSeededAtAll()
    {
        using var db = NewDb();
        var role = new IdentityRole("Super Admin") { NormalizedName = "SUPER ADMIN" };
        db.Roles.Add(role);
        db.RoleProfiles.Add(new RoleProfile { RoleId = role.Id, Tag = RoleTags.SuperAdmin });
        await db.SaveChangesAsync();

        Assert.True(await NewService(db).HasPermissionForRoleNameAsync("Super Admin", "AnythingAtAll", "EvenMadeUp"));
    }
}
