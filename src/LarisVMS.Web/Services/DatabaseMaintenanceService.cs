using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Services;

/// <summary>
/// Nightly index and statistics upkeep, at about 3 AM server time. Nothing else maintains the
/// database: Segments and MotionSpans take inserts in the middle of every camera's key range (their
/// clustered index is (CameraId, StartUtc)), and archiving lengthens a segment's FilePath in place,
/// so their pages split constantly — measured live at 99% fragmentation with pages half empty, so
/// every scan read twice the pages it needed.
///
/// Each index over <see cref="MinPages"/> pages is rebuilt when badly fragmented and reorganized
/// when mildly so (<see cref="Decide"/>); the busiest clustered indexes are rebuilt leaving 10% free
/// per page for those mid-range inserts. Rebuilds run online where the edition allows it
/// (Enterprise / Developer); elsewhere offline, which at this database's size takes seconds.
/// Statistics are refreshed afterwards. Needs the app's database login to own the database, as it
/// already must for migrations; without that it logs a warning and tries again the next night.
/// </summary>
public class DatabaseMaintenanceService(IServiceScopeFactory scopeFactory, ILogger<DatabaseMaintenanceService> logger) : BackgroundService
{
    internal const int MinPages = 1000;
    internal const double RebuildAbove = 30;
    internal const double ReorganizeAbove = 5;
    internal const int BusyFillFactor = 90;
    private static readonly TimeSpan RunAt = TimeSpan.FromHours(3);

    // Tables whose clustered index takes inserts mid-range: rebuilt with free space per page.
    private static readonly HashSet<string> BusyTables = new(StringComparer.OrdinalIgnoreCase) { "Segments", "MotionSpans", "CameraEvents" };

    public enum IndexAction { None, Reorganize, Rebuild }

    /// <summary>What to do with one index, from its fragmentation (percent) and size in pages.</summary>
    internal static IndexAction Decide(double fragmentationPercent, long pages)
    {
        if (pages < MinPages) return IndexAction.None;
        if (fragmentationPercent > RebuildAbove) return IndexAction.Rebuild;
        return fragmentationPercent > ReorganizeAbove ? IndexAction.Reorganize : IndexAction.None;
    }

    /// <summary>The ALTER INDEX statement for one index. Names come from the catalog, and are still
    /// bracket-quoted.</summary>
    internal static string Statement(string schema, string table, string index, bool clustered, IndexAction action, bool online)
    {
        var target = $"{Quote(index)} ON {Quote(schema)}.{Quote(table)}";
        if (action == IndexAction.Reorganize) return $"ALTER INDEX {target} REORGANIZE";
        var options = new List<string>();
        if (online) options.Add("ONLINE = ON");
        if (clustered && BusyTables.Contains(table)) options.Add($"FILLFACTOR = {BusyFillFactor}");
        return options.Count == 0 ? $"ALTER INDEX {target} REBUILD" : $"ALTER INDEX {target} REBUILD WITH ({string.Join(", ", options)})";
    }

    internal static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";

    /// <summary>How long until the next <see cref="RunAt"/>, local time.</summary>
    internal static TimeSpan UntilNextRun(DateTime nowLocal)
    {
        var next = nowLocal.Date + RunAt;
        if (next <= nowLocal) next = next.AddDays(1);
        return next - nowLocal;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(UntilNextRun(DateTime.Now), stoppingToken); }
            catch (OperationCanceledException) { break; }

            try
            {
                using var scope = scopeFactory.CreateScope();
                await RunAsync(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), stoppingToken);
            }
            catch (SqlException ex) when (ex.Number is 1088 or 15247 or 297 or 229 or 262)
            {
                logger.LogWarning(ex, "Database maintenance skipped — the app's database login can't alter indexes. Will try again tomorrow.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Database maintenance failed — will try again tomorrow.");
            }
        }
    }

    private sealed record IndexInfo(string Schema, string Table, string Index, bool Clustered, double Fragmentation, long Pages);

    private async Task RunAsync(ApplicationDbContext db, CancellationToken ct)
    {
        if (!db.Database.IsSqlServer()) return;
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(30));

        var online = await db.Database.SqlQueryRaw<int>("SELECT CAST(SERVERPROPERTY('EngineEdition') AS int) AS [Value]").SingleAsync(ct) == 3;
        var indexes = await db.Database.SqlQueryRaw<IndexInfo>("""
            SELECT s.name AS [Schema], t.name AS [Table], i.name AS [Index], CAST(CASE WHEN i.type = 1 THEN 1 ELSE 0 END AS bit) AS [Clustered],
                   ps.avg_fragmentation_in_percent AS [Fragmentation], ps.page_count AS [Pages]
            FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, 'LIMITED') ps
            JOIN sys.indexes i ON i.object_id = ps.object_id AND i.index_id = ps.index_id
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE i.name IS NOT NULL AND ps.alloc_unit_type_desc = 'IN_ROW_DATA' AND t.is_ms_shipped = 0
            """).ToListAsync(ct);

        var done = new List<string>();
        foreach (var ix in indexes.OrderByDescending(i => i.Pages))
        {
            var action = Decide(ix.Fragmentation, ix.Pages);
            if (action == IndexAction.None) continue;
            await db.Database.ExecuteSqlRawAsync(Statement(ix.Schema, ix.Table, ix.Index, ix.Clustered, action, online), ct);
            done.Add($"{ix.Index} {(action == IndexAction.Rebuild ? "rebuilt" : "reorganized")} ({ix.Fragmentation:0}%)");
        }
        await db.Database.ExecuteSqlRawAsync("EXEC sp_updatestats", ct);

        logger.LogInformation("Database maintenance done: {Indexes}; statistics updated.",
            done.Count == 0 ? "no indexes needed attention" : string.Join(", ", done));
    }
}
