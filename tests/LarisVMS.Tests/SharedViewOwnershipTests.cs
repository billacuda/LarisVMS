using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Dtos;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>
/// A Private view stays its owner's (see ViewTreeServiceTests for role trees). And one export
/// request can't ask for an unlimited amount of video.
/// </summary>
public class SharedViewOwnershipTests
{
    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task SomeoneElsesPrivateViewStaysTheirsEvenForASuperAdmin()
    {
        using var db = NewDb();
        var views = new ViewService(db);
        var view = await views.CreateAsync("Private", "owner");
        Assert.Null(await views.GetVisibleToAsync(view.Id, "admin"));
        await Assert.ThrowsAsync<LarisVMS.Core.Interfaces.ViewTreeException>(() =>
            views.UpdateAsync(view.Id, "admin", "x", "{\"cells\":[]}", 0));
        await Assert.ThrowsAsync<LarisVMS.Core.Interfaces.ViewTreeException>(() => views.DeleteAsync(view.Id, "admin"));
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
