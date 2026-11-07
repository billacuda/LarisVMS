using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Auth;
using LarisVMS.Core.Entities;
using LarisVMS.Infrastructure.Auth;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>A key's effective permissions are its role's, narrowed by its own list. These cover both
/// places permissions are checked: the authorization handler and GetGrantedAsync.</summary>
public class ApiKeyLimitsTests
{
    private static ApplicationDbContext NewDb() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ClaimsPrincipal Key(string role, params (string, string)[]? limits)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "API key: test"),
            new(ClaimTypes.Role, role),
            new(ApiKeyScope.KeyIdClaim, "00000000-0000-0000-0000-000000000001")
        };
        if (limits is not null) claims.AddRange(ApiKeyScope.ClaimsFor(limits.ToHashSet()));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "ApiKey"));
    }

    private static ClaimsPrincipal UnlimitedKey(string role) => Key(role, null);

    private static async Task<ApplicationDbContext> SeedRoleAsync(string role, bool superAdmin = false, params (string Resource, string Action)[] granted)
    {
        var db = NewDb();
        var roleId = Guid.NewGuid().ToString();
        db.Roles.Add(new IdentityRole(role) { Id = roleId });
        if (superAdmin) db.RoleProfiles.Add(new RoleProfile { RoleId = roleId, Tag = RoleTags.SuperAdmin });
        foreach (var (resource, action) in granted)
            db.Permissions.Add(new Permission { Id = Guid.NewGuid(), RoleId = roleId, Resource = resource, Action = action });
        await db.SaveChangesAsync();
        return db;
    }

    private static async Task<bool> AuthorizedAsync(ApplicationDbContext db, ClaimsPrincipal user, string resource, string action)
    {
        var requirement = new PermissionRequirement(resource, action);
        var context = new AuthorizationHandlerContext([requirement], user, null);
        await new PermissionAuthorizationHandler(new PermissionService(db, null!)).HandleAsync(context);
        return context.HasSucceeded;
    }

    [Fact]
    public async Task ALimitedKeyIsRefusedAPermissionItsRoleHas()
    {
        using var db = await SeedRoleAsync("Ops", false, ("Cameras", "View"), ("Exports", "View"));
        var key = Key("Ops", ("Cameras", "View"));

        Assert.True(await AuthorizedAsync(db, key, "Cameras", "View"));
        Assert.False(await AuthorizedAsync(db, key, "Exports", "View"));
    }

    [Fact]
    public async Task ALimitCantGrantAPermissionTheRoleLacks()
    {
        using var db = await SeedRoleAsync("Ops", false, ("Cameras", "View"));

        Assert.False(await AuthorizedAsync(db, Key("Ops", ("Exports", "View")), "Exports", "View"));
    }

    [Fact]
    public async Task ALimitedKeyWithNothingTickedCanDoNothing()
    {
        using var db = await SeedRoleAsync("Ops", false, ("Cameras", "View"));

        Assert.False(await AuthorizedAsync(db, Key("Ops"), "Cameras", "View"));
    }

    [Fact]
    public async Task AnUnlimitedKeyHasItsWholeRole()
    {
        using var db = await SeedRoleAsync("Ops", false, ("Cameras", "View"), ("Exports", "View"));

        Assert.True(await AuthorizedAsync(db, UnlimitedKey("Ops"), "Exports", "View"));
    }

    [Fact]
    public async Task GrantedPermissionsAreNarrowedToo()
    {
        using var db = await SeedRoleAsync("Ops", false, ("Cameras", "View"), ("Exports", "View"));

        var granted = await new PermissionService(db, null!).GetGrantedAsync(Key("Ops", ("Cameras", "View")));

        Assert.True(granted.Has("Cameras", "View"));
        Assert.False(granted.Has("Exports", "View"));
    }

    [Fact]
    public async Task ALimitedKeyOnAnAdministratorRoleIsNotAnAdministrator()
    {
        using var db = await SeedRoleAsync("Admin", superAdmin: true);

        var granted = await new PermissionService(db, null!).GetGrantedAsync(Key("Admin", ("Cameras", "View")));

        Assert.False(granted.IsAdministrator);
        Assert.True(granted.Has("Cameras", "View"));
        Assert.False(granted.Has("Exports", "View"));
    }

    [Fact]
    public async Task AnUnlimitedKeyOnAnAdministratorRoleStaysAnAdministrator()
    {
        using var db = await SeedRoleAsync("Admin", superAdmin: true);

        Assert.True((await new PermissionService(db, null!).GetGrantedAsync(UnlimitedKey("Admin"))).IsAdministrator);
    }

    [Fact]
    public async Task KeysStoreAndReturnTheirLimits()
    {
        using var db = await SeedRoleAsync("Ops");
        var roleId = await db.Roles.Select(r => r.Id).SingleAsync();
        var service = new ApiKeyService(db);

        var (_, limitedRaw) = await service.GenerateAsync("limited", roleId, null, limits: [("Cameras", "View")]);
        var (_, emptyRaw) = await service.GenerateAsync("empty", roleId, null, limits: []);
        var (_, fullRaw) = await service.GenerateAsync("full", roleId, null);

        var limited = await service.AuthenticateAsync(limitedRaw);
        Assert.Equal("limited", limited!.Value.KeyName);
        Assert.Equal([("Cameras", "View")], limited.Value.Limits!);
        Assert.Empty((await service.AuthenticateAsync(emptyRaw))!.Value.Limits!);
        Assert.Null((await service.AuthenticateAsync(fullRaw))!.Value.Limits);
    }

    [Fact]
    public async Task EditingAKeyChangesItsLimitsButNotItsValue()
    {
        using var db = await SeedRoleAsync("Ops");
        var roleId = await db.Roles.Select(r => r.Id).SingleAsync();
        var service = new ApiKeyService(db);
        var (key, raw) = await service.GenerateAsync("before", roleId, null, limits: [("Cameras", "View")]);

        await service.UpdateAsync(key.Id, "after", [("Exports", "View")]);
        var edited = await service.AuthenticateAsync(raw);
        Assert.Equal("after", edited!.Value.KeyName);
        Assert.Equal([("Exports", "View")], edited.Value.Limits!);

        await service.UpdateAsync(key.Id, "after", null);
        Assert.Null((await service.AuthenticateAsync(raw))!.Value.Limits);
    }

    [Fact]
    public void EachKeyIsItsOwnActor()
    {
        var one = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ApiKeyScope.KeyIdClaim, "1")], "ApiKey"));
        var two = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ApiKeyScope.KeyIdClaim, "2")], "ApiKey"));
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user-1")], "Cookie"));

        Assert.Equal("apikey:1", one.ActorId());
        Assert.NotEqual(one.ActorId(), two.ActorId());
        Assert.Equal("user-1", user.ActorId());
        Assert.Null(new ClaimsPrincipal(new ClaimsIdentity()).ActorId());
    }
}
