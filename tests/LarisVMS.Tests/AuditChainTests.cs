using System.Text;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Security;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;
using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

/// <summary>
/// The audit log's hash chain: a clean log checks out, and each way of tampering with it directly in
/// the database (change, remove from the middle, either end, add, or rewrite the state row) is caught,
/// while retention's own removals aren't.
/// </summary>
public class AuditChainTests
{
    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<ApplicationDbContext> LogWithAsync(int entries)
    {
        var db = NewDb();
        var audit = new AuditService(db);
        for (var i = 1; i <= entries; i++)
            await audit.LogAsync($"Test.{i}", "user-1", "alice", "192.0.2.1", $"details {i}");
        return db;
    }

    private static async Task<AuditChainReport> CheckAsync(ApplicationDbContext db) =>
        await AuditChainStore.VerifyAsync(db, CancellationToken.None);

    private static async Task<List<AuditLog>> EntriesAsync(ApplicationDbContext db) =>
        await db.AuditLogs.OrderBy(l => l.Id).ToListAsync();

    [Fact]
    public async Task AnUntouchedLogChecksOut()
    {
        using var db = await LogWithAsync(5);

        var report = await CheckAsync(db);

        Assert.True(report.Ok, report.Problem.ToString());
        Assert.Equal(5, report.Sealed);
        Assert.Equal(0, report.Unsealed);
    }

    [Fact]
    public async Task AnEmptyLogChecksOut() =>
        Assert.True((await CheckAsync(NewDb())).Ok);

    [Fact]
    public async Task AChangedEntryIsFound()
    {
        using var db = await LogWithAsync(5);
        var third = (await EntriesAsync(db))[2];
        third.Details = "nothing to see";
        await db.SaveChangesAsync();

        var report = await CheckAsync(db);

        Assert.Equal(AuditChainProblem.EntryChanged, report.Problem);
        Assert.Equal(third.Id, report.AtId);
    }

    [Fact]
    public async Task ARehashedEntryIsFoundWithoutTheKey()
    {
        using var db = await LogWithAsync(3);
        var second = (await EntriesAsync(db))[1];
        second.Details = "nothing to see";
        second.Hash = AuditChain.ComputeHash(AuditChain.NewKey(), second.PreviousHash!, second);
        await db.SaveChangesAsync();

        Assert.Equal(AuditChainProblem.EntryChanged, (await CheckAsync(db)).Problem);
    }

    [Fact]
    public async Task AnEntryRemovedFromTheMiddleIsFound()
    {
        using var db = await LogWithAsync(5);
        var entries = await EntriesAsync(db);
        db.AuditLogs.Remove(entries[2]);
        await db.SaveChangesAsync();

        var report = await CheckAsync(db);

        Assert.Equal(AuditChainProblem.EntryMissing, report.Problem);
        Assert.Equal(entries[3].Id, report.AtId);
    }

    [Fact]
    public async Task TheNewestEntryRemovedIsFound()
    {
        using var db = await LogWithAsync(5);
        db.AuditLogs.Remove((await EntriesAsync(db))[^1]);
        await db.SaveChangesAsync();

        Assert.Equal(AuditChainProblem.NewestRemoved, (await CheckAsync(db)).Problem);
    }

    [Fact]
    public async Task TheOldestEntryRemovedOutsideRetentionIsFound()
    {
        using var db = await LogWithAsync(5);
        db.AuditLogs.Remove((await EntriesAsync(db))[0]);
        await db.SaveChangesAsync();

        Assert.Equal(AuditChainProblem.OldestRemoved, (await CheckAsync(db)).Problem);
    }

    [Fact]
    public async Task RetentionRemovingTheOldestEntriesIsExpected()
    {
        using var db = await LogWithAsync(5);
        var entries = await EntriesAsync(db);
        entries[0].OccurredAt = entries[1].OccurredAt = DateTime.UtcNow.AddDays(-60);
        // Moving the time breaks those two entries' hashes; they're the ones retention removes.
        await db.SaveChangesAsync();

        var removed = await AuditChainStore.PruneAsync(db, DateTime.UtcNow.AddDays(-30), CancellationToken.None);
        var report = await CheckAsync(db);

        Assert.Equal(2, removed);
        Assert.True(report.Ok, report.Problem.ToString());
        Assert.Equal(3, report.Sealed);

        // And the chain carries on from there.
        await new AuditService(db).LogAsync("Test.after", null, null, null);
        Assert.True((await CheckAsync(db)).Ok);
    }

    [Fact]
    public async Task RetentionRemovingEverythingStillChecksOut()
    {
        using var db = await LogWithAsync(3);
        foreach (var e in await EntriesAsync(db)) e.OccurredAt = DateTime.UtcNow.AddDays(-60);
        await db.SaveChangesAsync();

        await AuditChainStore.PruneAsync(db, DateTime.UtcNow.AddDays(-30), CancellationToken.None);
        Assert.True((await CheckAsync(db)).Ok);

        await new AuditService(db).LogAsync("Test.after", null, null, null);
        Assert.True((await CheckAsync(db)).Ok);
    }

    [Fact]
    public async Task EntriesFromBeforeTheChainAreCountedNotFlagged()
    {
        using var db = NewDb();
        db.AuditLogs.AddRange(new AuditLog { Action = "Old.1" }, new AuditLog { Action = "Old.2" });
        await db.SaveChangesAsync();
        await new AuditService(db).LogAsync("New", null, null, null);

        var report = await CheckAsync(db);

        Assert.True(report.Ok, report.Problem.ToString());
        Assert.Equal(2, report.Unsealed);
        Assert.Equal(1, report.Sealed);
    }

    [Fact]
    public async Task AnEntryAddedDirectlyIsFound()
    {
        using var db = await LogWithAsync(2);
        var forged = new AuditLog { Action = "Forged" };
        db.AuditLogs.Add(forged);
        await db.SaveChangesAsync();
        await new AuditService(db).LogAsync("Next", null, null, null);

        var report = await CheckAsync(db);

        Assert.Equal(AuditChainProblem.EntryUnsealed, report.Problem);
        Assert.Equal(forged.Id, report.AtId);
    }

    [Fact]
    public async Task RewritingTheStateRowIsFound()
    {
        using var db = await LogWithAsync(3);
        var entries = await EntriesAsync(db);
        db.AuditLogs.Remove(entries[^1]);
        // Hide the removal by pointing the state at the entry that's now last.
        var state = await db.AuditChainStates.SingleAsync();
        state.LastHash = entries[^2].Hash!;
        await db.SaveChangesAsync();

        Assert.Equal(AuditChainProblem.StateChanged, (await CheckAsync(db)).Problem);
    }

    [Fact]
    public async Task RemovingTheStateRowIsFound()
    {
        using var db = await LogWithAsync(2);
        db.AuditChainStates.Remove(await db.AuditChainStates.SingleAsync());
        await db.SaveChangesAsync();

        Assert.Equal(AuditChainProblem.StateChanged, (await CheckAsync(db)).Problem);
    }

    [Fact]
    public async Task AnotherContextsWritesAreChainedOnto()
    {
        // Two contexts on the same database: each append reads the state fresh.
        var name = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(name).Options;
        using var a = new ApplicationDbContext(options);
        using var b = new ApplicationDbContext(options);
        await new AuditService(a).LogAsync("A.1", null, null, null);
        await new AuditService(b).LogAsync("B.1", null, null, null);
        await new AuditService(a).LogAsync("A.2", null, null, null);

        Assert.True((await CheckAsync(a)).Ok);
    }

    [Fact]
    public void TimesAreKeptToTheMicrosecondSoMariaDbReadsBackTheSame()
    {
        var t = new DateTime(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc).AddTicks(1_234_567);
        Assert.Equal(1_234_560, AuditChain.NormalizeTime(t).Ticks % 10_000_000);
    }

    [Fact]
    public void FieldsCantBeShiftedIntoEachOther()
    {
        var key = AuditChain.NewKey();
        var a = new AuditLog { Action = "A", UserName = "b|c", OccurredAt = DateTime.UnixEpoch };
        var b = new AuditLog { Action = "A|b", UserName = "c", OccurredAt = DateTime.UnixEpoch };
        var c = new AuditLog { Action = "A", UserName = null, OccurredAt = DateTime.UnixEpoch };
        var d = new AuditLog { Action = "A", UserName = "", OccurredAt = DateTime.UnixEpoch };
        Assert.NotEqual(AuditChain.ComputeHash(key, "", a), AuditChain.ComputeHash(key, "", b));
        Assert.NotEqual(AuditChain.ComputeHash(key, "", c), AuditChain.ComputeHash(key, "", d));
    }

    [Fact]
    public void ASyslogMessageIsOneRfc5424LineWithTheEntryAsJson()
    {
        var entry = new AuditLog
        {
            Id = 42, Action = "Login.Success", UserName = "alice", IpAddress = "192.0.2.1",
            Details = "line one\nline two \"quoted\"", OccurredAt = new DateTime(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc), Hash = "ab",
        };

        var message = AuditSyslogForwarder.Format(entry, "SERVER1", 8000);

        Assert.StartsWith("<109>1 2026-11-08T10:00:00.000000Z SERVER1 LarisVMS - audit - {", message);
        Assert.DoesNotContain('\n', message);
        using var json = System.Text.Json.JsonDocument.Parse(message[message.IndexOf('{')..]);
        Assert.Equal("line one\nline two \"quoted\"", json.RootElement.GetProperty("details").GetString());
        Assert.Equal(42, json.RootElement.GetProperty("id").GetInt64());
    }

    [Fact]
    public void LongDetailsAreShortenedToFitAUdpMessage()
    {
        var entry = new AuditLog { Id = 1, Action = "Settings.Update", Details = new string('é', 20_000), OccurredAt = DateTime.UtcNow };

        var message = AuditSyslogForwarder.Format(entry, "SERVER1", AuditSyslogForwarder.MaxUdpBytes);

        Assert.True(Encoding.UTF8.GetByteCount(message) <= AuditSyslogForwarder.MaxUdpBytes);
        using var json = System.Text.Json.JsonDocument.Parse(message[message.IndexOf('{')..]);
        Assert.EndsWith("…", json.RootElement.GetProperty("details").GetString());
    }

    [Fact]
    public void TcpFramingPrefixesTheLengthInBytes()
    {
        var framed = AuditSyslogForwarder.Frame("<109>1 é");
        Assert.Equal("9 <109>1 é", Encoding.UTF8.GetString(framed));
    }

    [Theory]
    [InlineData("syslog.example.com", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("fd00::5", true)]
    [InlineData("syslog.example.com:514", false)]
    [InlineData("syslog example", false)]
    [InlineData("syslog\nexample", false)]
    public void TheSyslogHostIsAHostOrAddressOnly(string host, bool valid) =>
        Assert.Equal(valid, AuditSyslogForwarder.IsValidHost(host));
}
