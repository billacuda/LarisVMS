using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Security;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Security;

namespace LarisVMS.Infrastructure.Services;

/// <summary>
/// Appends to, prunes, and checks the audit log's hash chain (<see cref="AuditChain"/>). Appends
/// and prunes take <see cref="Gate"/> (one web server writes the log) and, on a real database, the
/// state row's lock inside a transaction, so each entry chains onto exactly the one before it.
/// </summary>
public static class AuditChainStore
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Where the chain's anchor is kept outside the database; set by the web server at
    /// startup. Null (tests, tools) checks the chain without one.</summary>
    public static IAuditChainAnchors? Anchors { get; set; }

    public static async Task AppendAsync(ApplicationDbContext db, AuditLog entry, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            AuditChainAnchor? newAnchor = null;
            await InLockedTransactionAsync(db, async () =>
            {
                AuditChainState state;
                bool created;
                try
                {
                    (state, created) = await LoadStateAsync(db, ct);
                }
                catch (Exception ex) when (IsUnreadableKey(ex))
                {
                    // The key was changed outside LarisVMS. The entry is still written, unsealed, so
                    // nothing goes unrecorded, and the integrity check reports the changed state.
                    foreach (var tracked in db.ChangeTracker.Entries<AuditChainState>().ToList())
                        tracked.State = EntityState.Detached;
                    entry.OccurredAt = AuditChain.NormalizeTime(entry.OccurredAt);
                    db.AuditLogs.Add(entry);
                    await SaveOrDetachAsync(db, entry, null, ct);
                    return;
                }
                var key = Convert.FromBase64String(state.Key);
                entry.OccurredAt = AuditChain.NormalizeTime(entry.OccurredAt);
                entry.PreviousHash = state.LastHash;
                entry.Hash = AuditChain.ComputeHash(key, state.LastHash, entry);
                state.LastHash = entry.Hash;
                state.Seal = AuditChain.ComputeSeal(key, state.LastHash, state.PrunedThroughHash);
                db.AuditLogs.Add(entry);
                await SaveOrDetachAsync(db, entry, state, ct);
                // A new chain: remember its key and where it starts, outside the database.
                if (created) newAnchor = new AuditChainAnchor(AuditChain.KeyId(key), entry.Id - 1);
            }, ct);
            if (newAnchor is not null) TryWriteAnchor(newAnchor);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task SaveOrDetachAsync(ApplicationDbContext db, AuditLog entry, AuditChainState? state, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Not left tracked: a later save would write it onto a chain that has moved on.
            db.Entry(entry).State = EntityState.Detached;
            if (state is not null) db.Entry(state).State = EntityState.Detached;
            throw;
        }
    }

    /// <summary>The state row's key can't be used: not encrypted, not decryptable, or not base64.
    /// EF reports a converter's failure wrapped in an InvalidOperationException.</summary>
    private static bool IsUnreadableKey(Exception ex) =>
        ex is CryptographicException or FormatException
        || (ex is InvalidOperationException && ex.InnerException is CryptographicException or FormatException);

    private static void TryWriteAnchor(AuditChainAnchor anchor)
    {
        try { Anchors?.WriteIfMissing(anchor); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // Not fatal: the next check writes it once it can.
        }
    }

    /// <summary>Removes every entry up to the newest one older than <paramref name="cutoffUtc"/> and
    /// records where the chain now starts. Returns how many were removed.</summary>
    public static async Task<int> PruneAsync(ApplicationDbContext db, DateTime cutoffUtc, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var removed = 0;
            await InLockedTransactionAsync(db, async () =>
            {
                var throughId = await db.AuditLogs.Where(l => l.OccurredAt < cutoffUtc).MaxAsync(l => (long?)l.Id, ct);
                if (throughId is null) return;
                var throughHash = await db.AuditLogs.AsNoTracking().Where(l => l.Id == throughId)
                    .Select(l => l.Hash).SingleAsync(ct);

                if (db.Database.IsRelational())
                {
                    removed = await db.AuditLogs.Where(l => l.Id <= throughId).ExecuteDeleteAsync(ct);
                }
                else
                {
                    var expired = await db.AuditLogs.Where(l => l.Id <= throughId).ToListAsync(ct);
                    db.AuditLogs.RemoveRange(expired);
                    removed = expired.Count;
                }

                if (throughHash is not null)
                {
                    var (state, _) = await LoadStateAsync(db, ct);
                    state.PrunedThroughHash = throughHash;
                    state.Seal = AuditChain.ComputeSeal(Convert.FromBase64String(state.Key), state.LastHash, state.PrunedThroughHash);
                }
                await db.SaveChangesAsync(ct);
            }, ct);
            return removed;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Checks every entry up to the newest one at the moment of the check.</summary>
    public static async Task<AuditChainReport> VerifyAsync(ApplicationDbContext db, CancellationToken ct)
    {
        AuditChainState? state;
        byte[]? key = null;
        long? throughId;
        await Gate.WaitAsync(ct);
        try
        {
            // Read together, with no append in between, so the newest entry is the one the state names.
            try
            {
                state = await db.AuditChainStates.AsNoTracking().SingleOrDefaultAsync(s => s.Id == AuditChainState.SingletonId, ct);
                if (state is not null) key = Convert.FromBase64String(state.Key);
            }
            catch (Exception ex) when (IsUnreadableKey(ex))
            {
                state = null; // the key can't be read: treated as a changed state row
            }
            throughId = await db.AuditLogs.MaxAsync(l => (long?)l.Id, ct);
        }
        finally
        {
            Gate.Release();
        }

        AuditChainAnchor? anchor = null;
        var anchorUnreadable = false;
        try { anchor = Anchors?.Read(); }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            anchorUnreadable = true;
        }

        var verifier = new AuditChainVerifier(key, state, anchor);
        if (throughId is not null)
        {
            await foreach (var entry in db.AuditLogs.AsNoTracking().Where(l => l.Id <= throughId)
                               .OrderBy(l => l.Id).AsAsyncEnumerable().WithCancellation(ct))
                verifier.Add(entry);
        }
        var report = verifier.Finish(DateTime.UtcNow);
        if (anchorUnreadable && report.Ok) report = report with { Problem = AuditChainProblem.StateChanged };

        // A chain from before the anchor existed: anchored once it checks out.
        if (Anchors is not null && anchor is null && !anchorUnreadable && report.Ok && state is not null && key is not null)
        {
            var firstSealed = await db.AuditLogs.Where(l => l.Hash != null).MinAsync(l => (long?)l.Id, ct);
            TryWriteAnchor(new AuditChainAnchor(AuditChain.KeyId(key), (firstSealed ?? (throughId ?? 0) + 1) - 1));
        }
        return report;
    }

    private static async Task InLockedTransactionAsync(ApplicationDbContext db, Func<Task> work, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            await work();
            return;
        }

        var own = db.Database.CurrentTransaction is null;
        await using var tx = own ? await db.Database.BeginTransactionAsync(ct) : null;
        // Row lock until commit, on SQL Server and MariaDB alike.
        await db.AuditChainStates.Where(s => s.Id == AuditChainState.SingletonId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LockVersion, x => x.LockVersion + 1), ct);
        await work();
        if (tx is not null) await tx.CommitAsync(ct);
    }

    /// <summary>The state row, read fresh (a copy this context tracked earlier may be out of date),
    /// and created with a new key the first time (or after it was removed, which the anchor
    /// catches).</summary>
    private static async Task<(AuditChainState State, bool Created)> LoadStateAsync(ApplicationDbContext db, CancellationToken ct)
    {
        foreach (var tracked in db.ChangeTracker.Entries<AuditChainState>().ToList())
            tracked.State = EntityState.Detached;

        var state = await db.AuditChainStates.SingleOrDefaultAsync(s => s.Id == AuditChainState.SingletonId, ct);
        if (state is not null) return (state, false);

        var key = AuditChain.NewKey();
        state = new AuditChainState
        {
            Key = Convert.ToBase64String(key),
            LastHash = "",
            Seal = AuditChain.ComputeSeal(key, "", null),
        };
        db.AuditChainStates.Add(state);
        return (state, true);
    }
}
