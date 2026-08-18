using Microsoft.EntityFrameworkCore;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>
/// The server-backed replacement for the six preferences that used to live only in localStorage —
/// worth its own coverage since a user's preferences following them across devices/logins is the
/// entire point of this table existing.
/// </summary>
public class UserPreferenceServiceTests
{
    private static (ApplicationDbContext Db, UserPreferenceService Service) NewService()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new ApplicationDbContext(options);
        return (db, new UserPreferenceService(db));
    }

    [Fact]
    public async Task AUserWithNoPreferencesGetsAnEmptyMap()
    {
        var (_, service) = NewService();
        var result = await service.GetAllAsync("user-1");
        Assert.Empty(result);
    }

    [Fact]
    public async Task ASetPreferenceIsReadableBackImmediately()
    {
        var (_, service) = NewService();
        await service.SetAsync("user-1", "theme", "dark");

        var result = await service.GetAllAsync("user-1");

        Assert.Equal("dark", result["theme"]);
    }

    [Fact]
    public async Task SettingTheSameKeyAgainUpdatesInPlaceRatherThanDuplicating()
    {
        var (db, service) = NewService();
        await service.SetAsync("user-1", "theme", "dark");
        await service.SetAsync("user-1", "theme", "light");

        var result = await service.GetAllAsync("user-1");

        Assert.Equal("light", result["theme"]);
        Assert.Equal(1, await db.UserPreferences.CountAsync());
    }

    [Fact]
    public async Task PreferencesAreScopedPerUserAndNeverLeakAcrossUsers()
    {
        var (_, service) = NewService();
        await service.SetAsync("user-1", "theme", "dark");
        await service.SetAsync("user-2", "theme", "light");

        var user1 = await service.GetAllAsync("user-1");
        var user2 = await service.GetAllAsync("user-2");

        Assert.Equal("dark", user1["theme"]);
        Assert.Equal("light", user2["theme"]);
    }

    [Fact]
    public async Task DifferentKeysForTheSameUserCoexistIndependently()
    {
        var (_, service) = NewService();
        await service.SetAsync("user-1", "theme", "dark");
        await service.SetAsync("user-1", "lastViewId", "abc-123");

        var result = await service.GetAllAsync("user-1");

        Assert.Equal(2, result.Count);
        Assert.Equal("dark", result["theme"]);
        Assert.Equal("abc-123", result["lastViewId"]);
    }
}
