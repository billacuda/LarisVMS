using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Security;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Security;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>
/// The audit chain's anchor, kept outside the database: someone who can only write to the database
/// can't swap the chain's key, or remove the state row and the hashes, and have it pass as a clean
/// new chain. And keys that have only ever been stored encrypted refuse a plain value planted there.
/// </summary>
public class AuditChainAnchorTests
{
    private static async Task<(ApplicationDbContext Db, byte[] Key, List<AuditLog> Entries)> LogAsync(int count)
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var audit = new AuditService(db);
        for (var i = 1; i <= count; i++)
            await audit.LogAsync($"Test.{i}", "user-1", "alice", "192.0.2.1", $"details {i}");
        var state = await db.AuditChainStates.AsNoTracking().SingleAsync();
        return (db, Convert.FromBase64String(state.Key), await db.AuditLogs.OrderBy(l => l.Id).ToListAsync());
    }

    private static AuditChainReport Check(byte[]? key, AuditChainState? state, AuditChainAnchor? anchor, IEnumerable<AuditLog> entries)
    {
        var verifier = new AuditChainVerifier(key, state, anchor);
        foreach (var e in entries) verifier.Add(e);
        return verifier.Finish(DateTime.UtcNow);
    }

    [Fact]
    public async Task AMatchingAnchorChecksOut()
    {
        var (db, key, entries) = await LogAsync(3);
        var state = await db.AuditChainStates.AsNoTracking().SingleAsync();
        var anchor = new AuditChainAnchor(AuditChain.KeyId(key), entries[0].Id - 1);

        Assert.True(Check(key, state, anchor, entries).Ok);
    }

    [Fact]
    public async Task AChainRewrittenWithAnotherKeyIsFound()
    {
        // Every hash and the seal recomputed with a key of the attacker's choosing: consistent in
        // itself, but not the key this server started the chain with.
        var (db, key, entries) = await LogAsync(3);
        var anchor = new AuditChainAnchor(AuditChain.KeyId(key), entries[0].Id - 1);
        var theirs = AuditChain.NewKey();
        var previous = "";
        foreach (var e in entries)
        {
            e.PreviousHash = previous;
            e.Hash = AuditChain.ComputeHash(theirs, previous, e);
            previous = e.Hash;
        }
        var state = new AuditChainState { Key = Convert.ToBase64String(theirs), LastHash = previous, Seal = AuditChain.ComputeSeal(theirs, previous, null) };

        Assert.Equal(AuditChainProblem.StateChanged, Check(theirs, state, anchor, entries).Problem);
    }

    [Fact]
    public async Task RemovingTheStateRowAndEveryHashIsFound()
    {
        var (_, key, entries) = await LogAsync(3);
        var anchor = new AuditChainAnchor(AuditChain.KeyId(key), entries[0].Id - 1);
        foreach (var e in entries) { e.Hash = null; e.PreviousHash = null; }

        Assert.Equal(AuditChainProblem.StateChanged, Check(null, null, anchor, entries).Problem);
    }

    [Fact]
    public async Task AnUnsealedEntryAfterTheChainStartedIsFoundEvenFirst()
    {
        var (db, key, entries) = await LogAsync(3);
        var state = await db.AuditChainStates.AsNoTracking().SingleAsync();
        var anchor = new AuditChainAnchor(AuditChain.KeyId(key), entries[0].Id - 1);
        var planted = new AuditLog { Id = entries[0].Id - 1, Action = "Old.Entry", OccurredAt = DateTime.UtcNow };
        var inserted = new AuditLog { Id = entries[^1].Id + 1, Action = "Planted", OccurredAt = DateTime.UtcNow };

        // An entry from before the chain started is fine; one after it isn't, wherever it sits.
        Assert.True(Check(key, state, anchor, [planted, .. entries]).Ok);
        var report = Check(key, state, anchor with { SealedAfterId = planted.Id - 1 }, [planted, .. entries]);
        Assert.Equal(AuditChainProblem.EntryUnsealed, report.Problem);
        Assert.Equal(AuditChainProblem.EntryUnsealed, Check(key, state, anchor, [.. entries, inserted]).Problem);
    }

    [Fact]
    public void TheAnchorFileIsWrittenOnceAndNeverReplaced()
    {
        var path = Path.Combine(Path.GetTempPath(), "larisvms-anchor-" + Guid.NewGuid().ToString("N"), "audit-chain.dat");
        try
        {
            var file = new AuditChainAnchorFile(path);
            Assert.Null(file.Read());

            file.WriteIfMissing(new AuditChainAnchor("first", 10));
            file.WriteIfMissing(new AuditChainAnchor("second", 99));
            Assert.Equal(new AuditChainAnchor("first", 10), file.Read());

            File.WriteAllText(path, "not a record");
            Assert.Throws<CryptographicException>(() => file.Read());
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void AKeyColumnRefusesAPlainValuePlantedInTheDatabase()
    {
        var protector = new EphemeralDataProtectionProvider().CreateProtector("test");
        var stored = "enc:v1:" + protector.Protect("real-key");

        Assert.Equal("real-key", SecretProtection.UnprotectStrict(stored, protector));
        Assert.Throws<CryptographicException>(() => SecretProtection.UnprotectStrict("planted-key", protector));
        Assert.Null(SecretProtection.UnprotectStrict(null, protector));
    }
}
