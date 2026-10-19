using System.Linq.Expressions;
using LarisVMS.Core.Entities;
using LarisVMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LarisVMS.Infrastructure.Services;

/// <summary>EF-translatable form of <see cref="EvidenceLockRules.IsProtecting(EvidenceLock, DateTime)"/>,
/// shared by every server query that must leave locked footage alone.</summary>
public static class EvidenceLockQueries
{
    public static Expression<Func<EvidenceLock, bool>> Protecting(DateTime nowUtc)
    {
        var graceCutoff = nowUtc - EvidenceLockRules.RemovalGrace;
        return l => (l.RemovedAtUtc == null && (l.ExpiresAtUtc == null || l.ExpiresAtUtc > nowUtc))
                    || (l.RemovedAtUtc != null && l.RemovedAtUtc > graceCutoff);
    }

    /// <summary>
    /// For retention sweeps that expire rows "once the footage around them is gone" (bookmarks,
    /// motion spans, talk recordings): the earliest segment per camera that ISN'T under a lock —
    /// locked footage surviving past retention would otherwise pin that cut-off forever — plus the
    /// protected ranges themselves, so a row inside one is kept with the footage it belongs to.
    /// </summary>
    public static async Task<(Dictionary<Guid, DateTime> EarliestUnlockedStart, LarisVMS.Core.ProtectedFootage Locked)> LoadRetentionCutoffsAsync(
        ApplicationDbContext db, IReadOnlyCollection<Guid> cameraIds, DateTime nowUtc, CancellationToken ct)
    {
        var earliest = await db.Segments
            .Where(s => cameraIds.Contains(s.CameraId))
            .NotLocked(db, nowUtc)
            .GroupBy(s => s.CameraId)
            .Select(g => new { CameraId = g.Key, EarliestStartUtc = g.Min(s => s.StartUtc) })
            .ToDictionaryAsync(x => x.CameraId, x => x.EarliestStartUtc, ct);
        var ranges = await db.EvidenceLocks
            .Where(Protecting(nowUtc))
            .Where(l => cameraIds.Contains(l.CameraId))
            .Select(l => new LarisVMS.Core.Dtos.NodeConfigProtectedRangeDto(l.CameraId, l.FromUtc, l.ToUtc))
            .ToListAsync(ct);
        return (earliest, new LarisVMS.Core.ProtectedFootage(ranges));
    }

    public static Expression<Func<EvidenceLock, bool>> AndCamera(this Expression<Func<EvidenceLock, bool>> protecting, Guid cameraId)
    {
        var p = protecting.Parameters[0];
        var cameraMatch = Expression.Equal(Expression.Property(p, nameof(EvidenceLock.CameraId)), Expression.Constant(cameraId));
        return Expression.Lambda<Func<EvidenceLock, bool>>(Expression.AndAlso(protecting.Body, cameraMatch), p);
    }

    /// <summary>Segments of these rows that don't overlap any protecting lock on their camera.</summary>
    public static IQueryable<Segment> NotLocked(this IQueryable<Segment> segments, ApplicationDbContext db, DateTime nowUtc)
    {
        var graceCutoff = nowUtc - EvidenceLockRules.RemovalGrace;
        return segments.Where(s => !s.IsLocked && !db.EvidenceLocks.Any(l =>
            l.CameraId == s.CameraId && s.StartUtc <= l.ToUtc && s.EndUtc >= l.FromUtc
            && ((l.RemovedAtUtc == null && (l.ExpiresAtUtc == null || l.ExpiresAtUtc > nowUtc))
                || (l.RemovedAtUtc != null && l.RemovedAtUtc > graceCutoff))));
    }
}
