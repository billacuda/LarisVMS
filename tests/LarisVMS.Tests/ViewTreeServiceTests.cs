using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Auth;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>
/// View trees: each user's Private views, a tree per role that its members see and (with Views.Edit)
/// change, other roles' trees an admin lets a role open (open-only), unique names per tree, copies
/// named "- copy", folders that keep their views when deleted, and moves that need rights on both
/// ends.
/// </summary>
public class ViewTreeServiceTests
{
    private const string Layout = "{\"cells\":[]}";

    private sealed class World
    {
        public ApplicationDbContext Db { get; } = new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public ViewService Views => new(Db);
        public ViewAccessService Access => new(Db);

        public string Role(string name, bool viewsEdit = false, string? tag = null)
        {
            var id = Guid.NewGuid().ToString();
            Db.Roles.Add(new IdentityRole(name) { Id = id });
            if (tag is not null) Db.RoleProfiles.Add(new RoleProfile { RoleId = id, Tag = tag });
            if (viewsEdit) Db.Permissions.Add(new Permission { Id = Guid.NewGuid(), RoleId = id, Resource = "Views", Action = "Edit" });
            Db.SaveChanges();
            return id;
        }

        public void User(string id, params string[] roleIds)
        {
            Db.Users.Add(new ApplicationUser { Id = id, UserName = id });
            foreach (var r in roleIds) Db.UserRoles.Add(new IdentityUserRole<string> { UserId = id, RoleId = r });
            Db.SaveChanges();
        }
    }

    [Fact]
    public async Task MembersSeeTheirRolesTreeAndOnlyMembersWithViewsEditChangeIt()
    {
        var w = new World();
        var patrol = w.Role("Patrol", viewsEdit: true);
        var viewers = w.Role("Viewers");
        w.User("editor", patrol);
        w.User("watcher", viewers);
        w.User("both", patrol, viewers);
        var housing = await w.Views.CreateAsync("Housing", "editor", ViewTrees.Role(patrol));

        Assert.NotNull(await w.Views.GetVisibleToAsync(housing.Id, "both"));
        Assert.Null(await w.Views.GetVisibleToAsync(housing.Id, "watcher"));

        // In Viewers only (no Views.Edit anywhere): can't add to Viewers' tree.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => w.Views.CreateAsync("x", "watcher", ViewTrees.Role(viewers)));
        // But anyone may keep Private views.
        await w.Views.CreateAsync("Mine", "watcher");

        var tree = await w.Views.GetTreeAsync("both");
        Assert.Equal(["private", "role", "role"], tree.Trees.Select(t => t.Kind));
        Assert.True(tree.Trees.Single(t => t.Tree == ViewTrees.Role(patrol)).CanEdit);
    }

    [Fact]
    public async Task AGrantedRoleOpensAnotherRolesViewsButCantChangeThem()
    {
        var w = new World();
        var role1 = w.Role("Role 1", viewsEdit: true);
        var role2 = w.Role("Role 2", viewsEdit: true);
        w.User("one", role1);
        w.User("two", role2);
        var view = await w.Views.CreateAsync("Lobby", "one", ViewTrees.Role(role1));
        Assert.Null(await w.Views.GetVisibleToAsync(view.Id, "two"));

        await w.Access.GrantAsync(role1, role2, "admin");

        Assert.NotNull(await w.Views.GetVisibleToAsync(view.Id, "two"));
        Assert.False(await w.Views.CanEditAsync(view.Id, "two"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => w.Views.RenameAsync(view.Id, "two", "Mine"));
        var granted = (await w.Views.GetTreeAsync("two")).Trees.Single(t => t.Tree == ViewTrees.Role(role1));
        Assert.Equal("granted", granted.Kind);
        Assert.False(granted.CanEdit);

        // Not the other way round.
        var other = await w.Views.CreateAsync("Gate", "two", ViewTrees.Role(role2));
        Assert.Null(await w.Views.GetVisibleToAsync(other.Id, "one"));

        await w.Access.RevokeAsync(role1, role2);
        Assert.Null(await w.Views.GetVisibleToAsync(view.Id, "two"));
    }

    [Fact]
    public async Task ASuperAdminManagesEveryRolesTreeButNotPrivateViews()
    {
        var w = new World();
        var super = w.Role("Super Admin", tag: RoleTags.SuperAdmin);
        var patrol = w.Role("Patrol");
        w.User("admin", super);
        w.User("officer", patrol);
        var mine = await w.Views.CreateAsync("Mine", "officer");

        var shared = await w.Views.CreateAsync("Yard", "admin", ViewTrees.Role(patrol));
        await w.Views.RenameAsync(shared.Id, "admin", "Back yard");
        Assert.Null(await w.Views.GetVisibleToAsync(mine.Id, "admin"));
    }

    [Fact]
    public async Task NamesAreUniqueInATreeButNotAcrossTrees()
    {
        var w = new World();
        var a = w.Role("A", viewsEdit: true);
        var b = w.Role("B", viewsEdit: true);
        w.User("u", a, b);
        var folder = await w.Views.CreateFolderAsync("u", ViewTrees.Role(a), null, "Buildings");
        var first = await w.Views.CreateAsync("Lobby", "u", ViewTrees.Role(a));

        // Anywhere in the same tree, sub-folders included, a new view gets a free name; a rename can't clash.
        var second = await w.Views.CreateAsync("Lobby", "u", ViewTrees.Role(a), folder.Id);
        Assert.Equal("Lobby 2", second.Name);
        var ex = await Assert.ThrowsAsync<ViewTreeException>(() => w.Views.RenameAsync(second.Id, "u", "LOBBY"));
        Assert.Contains("LOBBY", ex.Message);
        var other = await w.Views.CreateFolderAsync("u", ViewTrees.Role(a), folder.Id, "Other");
        await Assert.ThrowsAsync<ViewTreeException>(() => w.Views.RenameFolderAsync(other.Id, "u", "buildings"));

        // Another tree may use the same name.
        Assert.Equal("Lobby", (await w.Views.CreateAsync("Lobby", "u", ViewTrees.Role(b))).Name);
        Assert.Equal("Lobby", (await w.Views.CreateAsync("Lobby", "u")).Name);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task ACopyKeepsItsNameOrBecomesCopyThenCopy2AndIsIndependent()
    {
        var w = new World();
        var a = w.Role("A", viewsEdit: true);
        var b = w.Role("B", viewsEdit: true);
        w.User("u", a, b);
        var source = await w.Views.CreateAsync("Housing - all cameras", "u", ViewTrees.Role(a));

        var inB = await w.Views.CopyAsync(source.Id, "u", ViewTrees.Role(b), null);
        Assert.Equal("Housing - all cameras", inB.Name);
        var again = await w.Views.CopyAsync(source.Id, "u", ViewTrees.Role(b), null);
        Assert.Equal("Housing - all cameras - copy", again.Name);
        var third = await w.Views.CopyAsync(source.Id, "u", ViewTrees.Role(b), null);
        Assert.Equal("Housing - all cameras - copy 2", third.Name);

        await w.Views.RenameAsync(inB.Id, "u", "Renamed");
        Assert.Equal("Housing - all cameras", (await w.Db.Views.SingleAsync(v => v.Id == source.Id)).Name);
    }

    [Fact]
    public async Task DeletingAFolderMovesWhatsInItUpALevel()
    {
        var w = new World();
        w.User("u");
        var outer = await w.Views.CreateFolderAsync("u", ViewTrees.Private, null, "Outer");
        var inner = await w.Views.CreateFolderAsync("u", ViewTrees.Private, outer.Id, "Inner");
        var deep = await w.Views.CreateFolderAsync("u", ViewTrees.Private, inner.Id, "Deep");
        var view = await w.Views.CreateAsync("Cam wall", "u", ViewTrees.Private, inner.Id);

        await w.Views.DeleteFolderAsync(inner.Id, "u");

        Assert.Equal(outer.Id, (await w.Db.Views.SingleAsync(v => v.Id == view.Id)).FolderId);
        Assert.Equal(outer.Id, (await w.Db.ViewFolders.SingleAsync(f => f.Id == deep.Id)).ParentId);
        Assert.False(await w.Db.ViewFolders.AnyAsync(f => f.Id == inner.Id));
    }

    [Fact]
    public async Task AFolderCantMoveIntoItsOwnBranch()
    {
        var w = new World();
        w.User("u");
        var outer = await w.Views.CreateFolderAsync("u", ViewTrees.Private, null, "Outer");
        var inner = await w.Views.CreateFolderAsync("u", ViewTrees.Private, outer.Id, "Inner");
        await Assert.ThrowsAsync<ViewTreeException>(() => w.Views.MoveFolderAsync(outer.Id, "u", ViewTrees.Private, inner.Id));
        await Assert.ThrowsAsync<ViewTreeException>(() => w.Views.MoveFolderAsync(outer.Id, "u", ViewTrees.Private, outer.Id));
    }

    [Fact]
    public async Task MovingNeedsRightsOnBothTreesAndAFreeName()
    {
        var w = new World();
        var a = w.Role("A", viewsEdit: true);
        var b = w.Role("B", viewsEdit: true);
        var readOnly = w.Role("C");
        w.User("u", a);
        w.User("v", b);
        await w.Access.GrantAsync(readOnly, a, "admin");
        var view = await w.Views.CreateAsync("Gate", "u", ViewTrees.Role(a));

        // Not a member of B, and C is only granted to A (open-only).
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => w.Views.MoveAsync(view.Id, "u", ViewTrees.Role(b), null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => w.Views.MoveAsync(view.Id, "u", ViewTrees.Role(readOnly), null));

        // A Private view of the same name blocks moving it there; renamed, it moves and becomes theirs.
        var mine = await w.Views.CreateAsync("Gate", "u");
        await Assert.ThrowsAsync<ViewTreeException>(() => w.Views.MoveAsync(view.Id, "u", ViewTrees.Private, null));
        await w.Views.RenameAsync(mine.Id, "u", "Old gate");
        await w.Views.MoveAsync(view.Id, "u", ViewTrees.Private, null);
        var moved = await w.Db.Views.SingleAsync(v => v.Id == view.Id);
        Assert.Null(moved.RoleId);
        Assert.Equal("u", moved.OwnerId);
    }

    [Fact]
    public async Task MovingAFolderToAnotherTreeTakesEverythingInIt()
    {
        var w = new World();
        var a = w.Role("A", viewsEdit: true);
        var b = w.Role("B", viewsEdit: true);
        w.User("u", a, b);
        var folder = await w.Views.CreateFolderAsync("u", ViewTrees.Role(a), null, "Site");
        var sub = await w.Views.CreateFolderAsync("u", ViewTrees.Role(a), folder.Id, "Floor 1");
        var view = await w.Views.CreateAsync("Hall", "u", ViewTrees.Role(a), sub.Id);

        await w.Views.MoveFolderAsync(folder.Id, "u", ViewTrees.Role(b), null);

        Assert.Equal(b, (await w.Db.Views.SingleAsync(v => v.Id == view.Id)).RoleId);
        Assert.Equal(b, (await w.Db.ViewFolders.SingleAsync(f => f.Id == sub.Id)).RoleId);
        Assert.Equal(sub.Id, (await w.Db.Views.SingleAsync(v => v.Id == view.Id)).FolderId);
    }

    [Fact]
    public async Task ADeletedRolesViewsMoveToTheAdminsPrivateViews()
    {
        var w = new World();
        var gone = w.Role("Gone", viewsEdit: true);
        var other = w.Role("Other");
        w.User("u", gone);
        w.User("admin");
        var view = await w.Views.CreateAsync("Lobby", "u", ViewTrees.Role(gone));
        await w.Views.CreateAsync("Lobby", "admin");
        await w.Access.GrantAsync(gone, other, "admin");

        await w.Views.MoveDeletedRoleTreeAsync(gone, "admin");

        var moved = await w.Db.Views.SingleAsync(v => v.Id == view.Id);
        Assert.Null(moved.RoleId);
        Assert.Equal("admin", moved.OwnerId);
        Assert.Equal("Lobby (2)", moved.Name);
        Assert.Empty(w.Db.ViewTreeGrants);
    }

    [Fact]
    public async Task OldDuplicateNamesAreMadeUniqueOnce()
    {
        var w = new World();
        w.User("u");
        w.Db.Views.AddRange(
            new View { Id = Guid.NewGuid(), Name = "Wall", OwnerId = "u", LayoutJson = Layout, CreatedAt = DateTime.UtcNow.AddDays(-2) },
            new View { Id = Guid.NewGuid(), Name = "wall", OwnerId = "u", LayoutJson = Layout, CreatedAt = DateTime.UtcNow.AddDays(-1) },
            new View { Id = Guid.NewGuid(), Name = "Wall", OwnerId = "someone else", LayoutJson = Layout });
        await w.Db.SaveChangesAsync();

        Assert.Equal(1, await w.Views.MakeNamesUniqueAsync());
        Assert.Equal(["Wall", "wall (2)"], (await w.Db.Views.Where(v => v.OwnerId == "u").OrderBy(v => v.CreatedAt).ToListAsync()).Select(v => v.Name));
        Assert.Equal(0, await w.Views.MakeNamesUniqueAsync());
    }

    [Fact]
    public async Task ToursCycleThroughTheViewsYouCanSee()
    {
        var w = new World();
        var a = w.Role("A", viewsEdit: true);
        w.User("u", a);
        w.User("other");
        var mine = await w.Views.CreateAsync("Mine", "u");
        await w.Views.UpdateAsync(mine.Id, "u", "Mine", Layout, 10);
        var theirs = await w.Views.CreateAsync("Theirs", "other");
        await w.Views.UpdateAsync(theirs.Id, "other", "Theirs", Layout, 10);

        Assert.Equal([mine.Id], (await w.Views.GetTourViewsAsync("u")).Select(v => v.Id));
    }
}
