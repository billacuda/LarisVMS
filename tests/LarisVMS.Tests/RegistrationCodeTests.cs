using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>
/// One-time registration codes (O11): each registers one node or proxy, isn't spent by a failed
/// registration, expires, and keeps working when the shared key is turned off — which stops the
/// shared key itself.
/// </summary>
public class RegistrationCodeTests
{
    private const string SharedKey = "SHARED-KEY-0123456789";

    private static ApplicationDbContext NewDb(string? name = null) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(name ?? Guid.NewGuid().ToString()).Options);

    private static Task<bool> AcceptAsync(ApplicationDbContext db, string? presented, bool sharedEnabled = true) =>
        RegistrationKeys.AcceptAsync(db, new Settings(sharedEnabled), presented, "NODE1", CancellationToken.None);

    [Fact]
    public async Task TheSharedKeyWorksUntilItsTurnedOff()
    {
        using var db = NewDb();
        Assert.True(await AcceptAsync(db, SharedKey));
        Assert.True(await AcceptAsync(db, " " + SharedKey + " "));
        Assert.False(await AcceptAsync(db, SharedKey, sharedEnabled: false));
        Assert.False(await AcceptAsync(db, "wrong"));
        Assert.False(await AcceptAsync(db, null));
    }

    [Fact]
    public async Task ACodeRegistersOnceAndRecordsWho()
    {
        using var db = NewDb();
        var (code, row) = await RegistrationKeys.CreateAsync(db, TimeSpan.FromHours(1), "admin", CancellationToken.None);

        Assert.True(await AcceptAsync(db, code, sharedEnabled: false));
        await db.SaveChangesAsync();
        Assert.False(await AcceptAsync(db, code, sharedEnabled: false));

        var saved = await db.RegistrationCodes.AsNoTracking().SingleAsync(c => c.Id == row.Id);
        Assert.NotNull(saved.UsedUtc);
        Assert.Equal("NODE1", saved.UsedBy);
        Assert.NotEqual(code, saved.CodeHash); // only the hash is kept
    }

    [Fact]
    public async Task ACodeIsntSpentIfTheRegistrationIsntSaved()
    {
        var name = Guid.NewGuid().ToString();
        string code;
        using (var db = NewDb(name))
        {
            (code, _) = await RegistrationKeys.CreateAsync(db, TimeSpan.FromHours(1), "admin", CancellationToken.None);
            Assert.True(await AcceptAsync(db, code));
            // no SaveChanges: the registration failed
        }
        using (var db = NewDb(name))
            Assert.True(await AcceptAsync(db, code));
    }

    [Fact]
    public async Task TwoRegistrationsRacingForOneCodeLetOnlyOneThrough()
    {
        var name = Guid.NewGuid().ToString();
        string code;
        using (var setup = NewDb(name))
            (code, _) = await RegistrationKeys.CreateAsync(setup, TimeSpan.FromHours(1), "admin", CancellationToken.None);

        using var a = NewDb(name);
        using var b = NewDb(name);
        Assert.True(await AcceptAsync(a, code));
        Assert.True(await AcceptAsync(b, code));
        await a.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => b.SaveChangesAsync());
    }

    [Fact]
    public async Task AnExpiredCodeIsRefused()
    {
        using var db = NewDb();
        var (code, row) = await RegistrationKeys.CreateAsync(db, TimeSpan.FromHours(1), "admin", CancellationToken.None);
        row.ExpiresUtc = DateTime.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();

        Assert.False(await AcceptAsync(db, code));
    }

    /// <summary>Only the two registration settings; the shared key is stored as it would be unencrypted.</summary>
    private sealed class Settings(bool sharedEnabled) : ISettingsResolver
    {
        public Task<string?> GetRawAsync(string key, Guid? cameraId = null, Guid? nodeId = null, CancellationToken ct = default) =>
            Task.FromResult(key == RegistrationKeys.SharedKeySettingKey ? SharedKey : null);

        public Task<T> GetAsync<T>(string key, T defaultValue, Guid? cameraId = null, Guid? nodeId = null, CancellationToken ct = default) =>
            Task.FromResult(key == RegistrationKeys.SharedKeyEnabledKey ? (T)(object)sharedEnabled : defaultValue);

        public Task<SettingScope?> GetSourceAsync(string key, Guid? cameraId = null, Guid? nodeId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetOwnOverrideAsync(SettingScope scope, Guid scopeId, string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetGlobalAsync(string key, string value, string? modifiedBy = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetOverrideAsync(SettingScope scope, Guid scopeId, string key, string? value, string? modifiedBy = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task InvalidateAsync() => Task.CompletedTask;
    }
}
