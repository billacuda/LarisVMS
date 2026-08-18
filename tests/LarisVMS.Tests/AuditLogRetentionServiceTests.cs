using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

/// <summary>
/// The audit trail never had a sweep at all before this — every row was kept forever. Worth pinning
/// down that the new default keeps that exact behavior (0 = forever) rather than silently starting
/// to delete a deployment's compliance history on upgrade, and that a real window actually removes
/// only the rows past it.
/// </summary>
public class AuditLogRetentionServiceTests
{
    private static ApplicationDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static async Task SeedAsync(ApplicationDbContext db, params DateTime[] occurredAtTimes)
    {
        foreach (var t in occurredAtTimes)
            db.AuditLogs.Add(new AuditLog { Action = "Test", OccurredAt = t });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task DefaultOfZeroKeepsEveryRowForever()
    {
        using var db = NewDb();
        await SeedAsync(db, DateTime.UtcNow.AddYears(-5), DateTime.UtcNow);

        await AuditLogRetentionService.SweepAsync(db, AuditLogRetentionService.DefaultRetentionDays, CancellationToken.None);

        Assert.Equal(2, await db.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task ANegativeValueAlsoKeepsEverything()
    {
        using var db = NewDb();
        await SeedAsync(db, DateTime.UtcNow.AddYears(-5));

        await AuditLogRetentionService.SweepAsync(db, -1, CancellationToken.None);

        Assert.Equal(1, await db.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task ARealWindowDeletesOnlyRowsOlderThanIt()
    {
        using var db = NewDb();
        var now = DateTime.UtcNow;
        await SeedAsync(db, now.AddDays(-40), now.AddDays(-5), now);

        await AuditLogRetentionService.SweepAsync(db, 14, CancellationToken.None);

        var remaining = await db.AuditLogs.Select(l => l.OccurredAt).ToListAsync();
        Assert.Equal(2, remaining.Count);
        Assert.DoesNotContain(remaining, t => t < now.AddDays(-14));
    }
}
