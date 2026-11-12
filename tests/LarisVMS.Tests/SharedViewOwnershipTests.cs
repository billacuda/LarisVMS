using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Dtos;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>
/// A shared view can be used by anyone who sees views, but changed or deleted only by its owner or an
/// administrator, so one user can't rearrange or un-share everyone else's. And one export request
/// can't ask for an unlimited amount of video.
/// </summary>
public class SharedViewOwnershipTests
{
    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task OnlyTheOwnerOrAnAdministratorChangesASharedView()
    {
        using var db = NewDb();
        var views = new ViewService(db);
        var view = await views.CreateAsync("Lobby", "owner");
        await views.UpdateAsync(view.Id, "owner", "Lobby", isShared: true, "{\"cells\":[]}", 0);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            views.UpdateAsync(view.Id, "someone", "Mine now", isShared: false, "{\"cells\":[]}", 0));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => views.DeleteAsync(view.Id, "someone", canManageAll: false));

        await views.UpdateAsync(view.Id, "admin", "Lobby (fixed)", isShared: true, "{\"cells\":[]}", 0, canManageAll: true);
        Assert.Equal("Lobby (fixed)", (await db.Views.SingleAsync()).Name);
        await views.DeleteAsync(view.Id, "admin", canManageAll: true);
        Assert.Empty(db.Views);
    }

    [Fact]
    public async Task SomeoneElsesPersonalViewStaysTheirsEvenForAnAdministrator()
    {
        using var db = NewDb();
        var views = new ViewService(db);
        var view = await views.CreateAsync("Private", "owner");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            views.UpdateAsync(view.Id, "admin", "x", isShared: true, "{\"cells\":[]}", 0, canManageAll: true));
    }

    [Theory]
    [InlineData(4, 24 * 7, null)]          // a few cameras for a week
    [InlineData(64, 15, null)]             // a whole site for a while
    [InlineData(65, 1, "cameras")]
    [InlineData(1, 24 * 32, "span")]
    [InlineData(10, 24 * 5, "total")]      // 1,200 camera-hours
    public void ExportsAreLimited(int cameras, int hours, string? over)
    {
        var from = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(over, ExportLimits.Check(cameras, from, from.AddHours(hours)));
    }
}
