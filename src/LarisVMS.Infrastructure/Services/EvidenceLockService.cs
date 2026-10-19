using System.Globalization;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

/// <summary>See IEvidenceLockService. Times in audit details are written as UTC with the zone named,
/// since an audit entry is read later by people in any time zone.</summary>
public class EvidenceLockService(ApplicationDbContext db, IAuditService audit) : IEvidenceLockService
{
    /// <summary>Clock allowance for "to" — the range may run up to now (the playhead is often at live).</summary>
    private static readonly TimeSpan FutureAllowance = TimeSpan.FromMinutes(5);

    public async Task<IReadOnlyList<EvidenceLockDto>> CreateAsync(CreateEvidenceLockRequest request, EvidenceLockActor actor, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var from = AsUtc(request.FromUtc);
        var to = AsUtc(request.ToUtc);
        var reason = (request.Reason ?? "").Trim();
        var cameraIds = request.CameraIds.Distinct().ToList();

        if (cameraIds.Count == 0) throw new EvidenceLockValidationException("Choose at least one camera.");
        if (reason.Length == 0) throw new EvidenceLockValidationException("Enter a reason for the lock.");
        if (reason.Length > 500) throw new EvidenceLockValidationException("The reason can be at most 500 characters.");
        if (to <= from) throw new EvidenceLockValidationException("The end of the range must be after its start.");
        if (to > now + FutureAllowance) throw new EvidenceLockValidationException("The range can't end in the future.");
        if (to - from > EvidenceLockRules.MaxRange)
            throw new EvidenceLockValidationException($"One lock can cover at most {EvidenceLockRules.MaxRange.TotalDays:0} days of footage.");

        var expiresAt = ResolveExpiry(request.Duration, request.CustomUntilUtc, now);

        var cameras = await db.Cameras.AsNoTracking().Where(c => cameraIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name }).ToListAsync(ct);
        if (cameras.Count != cameraIds.Count) throw new EvidenceLockValidationException("One or more cameras no longer exist.");

        var locks = cameras.Select(c => new EvidenceLock
        {
            Id = Guid.NewGuid(), CameraId = c.Id, FromUtc = from, ToUtc = to, Reason = reason, ExpiresAtUtc = expiresAt,
            CreatedByUserId = actor.UserId, CreatedByUserName = actor.UserName, CreatedAtUtc = now,
        }).ToList();
        db.EvidenceLocks.AddRange(locks);
        await db.SaveChangesAsync(ct);

        foreach (var l in locks)
        {
            var name = cameras.First(c => c.Id == l.CameraId).Name;
            await audit.LogAsync("EvidenceLock.Create", actor.UserId, actor.UserName, actor.IpAddress,
                $"Locked {name} footage {Utc(l.FromUtc)} to {Utc(l.ToUtc)}, {DescribeExpiry(l.ExpiresAtUtc)}. Reason: {l.Reason}", ct);
        }

        return await ToDtosAsync(locks, actor, ct);
    }

    public async Task<EvidenceLockDto> RemoveAsync(Guid id, string reason, EvidenceLockActor actor, CancellationToken ct = default)
    {
        var l = await LoadForChangeAsync(id, actor, ct);
        reason = (reason ?? "").Trim();
        if (reason.Length == 0) throw new EvidenceLockValidationException("Enter a reason for removing the lock.");
        if (reason.Length > 500) throw new EvidenceLockValidationException("The reason can be at most 500 characters.");
        if (l.RemovedAtUtc is not null) throw new EvidenceLockValidationException("This lock has already been removed.");

        var now = DateTime.UtcNow;
        l.RemovedAtUtc = now;
        l.RemovedByUserId = actor.UserId;
        l.RemovedByUserName = actor.UserName;
        l.RemovalReason = reason;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("EvidenceLock.Remove", actor.UserId, actor.UserName, actor.IpAddress,
            $"Removed the lock on {l.Camera.Name} footage {Utc(l.FromUtc)} to {Utc(l.ToUtc)} (placed by {l.CreatedByUserName ?? l.CreatedByUserId}). " +
            $"The footage stays protected until {Utc(now + EvidenceLockRules.RemovalGrace)}. Reason: {reason}", ct);

        return (await ToDtosAsync([l], actor, ct))[0];
    }

    public async Task<EvidenceLockDto> RestoreAsync(Guid id, EvidenceLockActor actor, CancellationToken ct = default)
    {
        var l = await LoadForChangeAsync(id, actor, ct);
        var now = DateTime.UtcNow;
        if (EvidenceLockRules.StatusOf(l, now) != EvidenceLockStatus.RemovedInGrace)
            throw new EvidenceLockValidationException("Only a lock removed in the last 24 hours can be restored.");

        l.RemovedAtUtc = null;
        l.RemovedByUserId = null;
        l.RemovedByUserName = null;
        l.RemovalReason = null;
        l.RemovalNotifiedAtUtc = null;
        l.GraceEndedAtUtc = null;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("EvidenceLock.Restore", actor.UserId, actor.UserName, actor.IpAddress,
            $"Restored the lock on {l.Camera.Name} footage {Utc(l.FromUtc)} to {Utc(l.ToUtc)}, {DescribeExpiry(l.ExpiresAtUtc)}.", ct);

        return (await ToDtosAsync([l], actor, ct))[0];
    }

    public async Task<IReadOnlyList<EvidenceLockDto>> ListAsync(IReadOnlyCollection<Guid>? cameraIds, DateTime? fromUtc, DateTime? toUtc,
        bool includeInactive, EvidenceLockActor? viewer, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var q = db.EvidenceLocks.AsNoTracking().Include(l => l.Camera).AsQueryable();
        if (cameraIds is not null) q = q.Where(l => cameraIds.Contains(l.CameraId));
        if (fromUtc is { } f) { var from = AsUtc(f); q = q.Where(l => l.ToUtc >= from); }
        if (toUtc is { } t) { var to = AsUtc(t); q = q.Where(l => l.FromUtc <= to); }
        if (!includeInactive) q = q.Where(EvidenceLockQueries.Protecting(now));

        var locks = await q.OrderByDescending(l => l.CreatedAtUtc).Take(1000).ToListAsync(ct);
        return await ToDtosAsync(locks, viewer, ct);
    }

    public Task<List<NodeConfigProtectedRangeDto>> GetProtectedRangesAsync(IReadOnlyCollection<Guid>? cameraIds, CancellationToken ct = default)
    {
        var q = db.EvidenceLocks.AsNoTracking().Where(EvidenceLockQueries.Protecting(DateTime.UtcNow));
        if (cameraIds is not null) q = q.Where(l => cameraIds.Contains(l.CameraId));
        return q.Select(l => new NodeConfigProtectedRangeDto(l.CameraId, l.FromUtc, l.ToUtc)).ToListAsync(ct);
    }

    /// <summary>"1d"…"30d" from now, "custom" (a future date), or "indefinite" (null).</summary>
    internal static DateTime? ResolveExpiry(string? duration, DateTime? customUntilUtc, DateTime nowUtc)
    {
        var key = (duration ?? "").Trim().ToLowerInvariant();
        if (EvidenceLockRules.Durations.TryGetValue(key, out var span)) return nowUtc + span;
        if (key == "indefinite") return null;
        if (key == "custom")
        {
            if (customUntilUtc is not { } until) throw new EvidenceLockValidationException("Choose the date the lock should end.");
            until = AsUtc(until);
            if (until <= nowUtc.AddHours(1)) throw new EvidenceLockValidationException("The end date must be in the future.");
            return until;
        }
        throw new EvidenceLockValidationException("Choose how long to keep the footage.");
    }

    private async Task<EvidenceLock> LoadForChangeAsync(Guid id, EvidenceLockActor actor, CancellationToken ct)
    {
        var l = await db.EvidenceLocks.Include(x => x.Camera).FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new KeyNotFoundException("Evidence lock not found.");
        if (!CanManage(l, actor))
            throw new EvidenceLockForbiddenException("Only the person who placed this lock, or a Super Admin, can change it.");
        return l;
    }

    internal static bool CanManage(EvidenceLock l, EvidenceLockActor? actor)
        => actor is not null && (actor.IsSuperAdmin || string.Equals(l.CreatedByUserId, actor.UserId, StringComparison.Ordinal));

    private async Task<List<EvidenceLockDto>> ToDtosAsync(IReadOnlyList<EvidenceLock> locks, EvidenceLockActor? viewer, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var cameraIds = locks.Select(l => l.CameraId).Distinct().ToList();
        var names = locks.Any(l => l.Camera is null)
            ? await db.Cameras.AsNoTracking().Where(c => cameraIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct)
            : locks.GroupBy(l => l.CameraId).ToDictionary(g => g.Key, g => g.First().Camera.Name);

        var result = new List<EvidenceLockDto>(locks.Count);
        foreach (var l in locks)
        {
            var size = await db.Segments.AsNoTracking()
                .Where(s => s.CameraId == l.CameraId && s.StartUtc <= l.ToUtc && s.EndUtc >= l.FromUtc)
                .SumAsync(s => (long?)s.SizeBytes, ct) ?? 0;
            result.Add(new EvidenceLockDto(l.Id, l.CameraId, names.GetValueOrDefault(l.CameraId) ?? "(deleted camera)",
                l.FromUtc, l.ToUtc, l.Reason, l.ExpiresAtUtc, l.CreatedByUserId, l.CreatedByUserName, l.CreatedAtUtc,
                l.RemovedAtUtc, l.RemovedByUserName, l.RemovalReason,
                EvidenceLockRules.StatusOf(l, now).ToString(), EvidenceLockRules.ProtectedUntil(l), size, CanManage(l, viewer)));
        }
        return result;
    }

    private static string DescribeExpiry(DateTime? expiresAtUtc)
        => expiresAtUtc is { } e ? $"kept until {Utc(e)}" : "kept indefinitely";

    private static string Utc(DateTime value) => AsUtc(value).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

    private static DateTime AsUtc(DateTime value)
        => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
