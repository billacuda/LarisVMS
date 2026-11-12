using LarisVMS.Core.Interfaces;
using LarisVMS.Core.Localization;
using LarisVMS.Core.Security;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Web.Services;

/// <summary>
/// Checks the audit log's hash chain (<see cref="AuditChainStore.VerifyAsync"/>): on request from
/// the audit log page, and on every retention cycle. The latest result is kept for the page. A
/// problem not reported before is logged, written to the audit log, and sent to Super Admins.
/// </summary>
public static class AuditIntegrity
{
    public const string AuditLogPage = "/Logs/AuditLogs";

    private static readonly object Sync = new();
    private static (AuditChainProblem Problem, long? At)? _reported;

    public static AuditChainReport? Last { get; private set; }

    public static async Task<AuditChainReport> CheckAsync(IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        var db = services.GetRequiredService<ApplicationDbContext>();
        var report = await AuditChainStore.VerifyAsync(db, ct);
        Last = report;

        bool isNew;
        lock (Sync)
        {
            var key = report.Ok ? ((AuditChainProblem, long?)?)null : (report.Problem, report.AtId);
            isNew = key is not null && !Equals(_reported, key);
            _reported = key;
        }
        if (!isNew) return report;

        logger.LogError("Audit log integrity check failed: {Problem} at entry {Id}.", report.Problem, report.AtId);
        var problem = Describe(report);
        try
        {
            await services.GetRequiredService<IAuditService>().LogAsync("AuditLog.IntegrityFailed", null, "system", null,
                $"{report.Problem} at entry {report.AtId}", ct);
            await services.GetRequiredService<INotificationService>().NotifySuperAdminsAsync(
                () => Loc.T("logs.auditLogs.integrityNoticeTitle"), () => Describe(report), AuditLogPage, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not report the audit log integrity problem ({Problem}).", problem);
        }
        return report;
    }

    /// <summary>The result in the reader's language.</summary>
    public static string Describe(AuditChainReport report) => report.Problem switch
    {
        AuditChainProblem.None => Loc.T("logs.auditLogs.integrityOk", new { count = report.Sealed }),
        AuditChainProblem.EntryChanged => Loc.T("logs.auditLogs.integrityEntryChanged", new { id = report.AtId }),
        AuditChainProblem.EntryMissing => Loc.T("logs.auditLogs.integrityEntryMissing", new { id = report.AtId }),
        AuditChainProblem.EntryUnsealed => Loc.T("logs.auditLogs.integrityEntryUnsealed", new { id = report.AtId }),
        AuditChainProblem.OldestRemoved => Loc.T("logs.auditLogs.integrityOldestRemoved", new { id = report.AtId }),
        AuditChainProblem.NewestRemoved => Loc.T("logs.auditLogs.integrityNewestRemoved", new { id = report.AtId }),
        _ => Loc.T("logs.auditLogs.integrityStateChanged"),
    };
}
