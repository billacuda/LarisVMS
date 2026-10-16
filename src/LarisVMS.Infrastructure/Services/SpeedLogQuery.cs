using LarisVMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LarisVMS.Infrastructure.Services;

/// <summary>One speed-trap measurement in the speed log.</summary>
public sealed record SpeedLogEntry(long Id, DateTime AtUtc, Guid CameraId, string CameraName, Guid? TrapId, string TrapName,
    string Label, string? Color, double SpeedKph, double? LimitKph)
{
    public bool OverLimit => LimitKph is { } limit && SpeedKph > limit;
}

/// <summary>
/// The speed log behind the dashboard's Speed log widget and report: every object a speed trap
/// measured, paged in the database (the history can be long), narrowed to the cameras the viewer may
/// see. Free text matches camera, trap, object label or color. Reads the filtered
/// (CameraId, SpeedMeasuredAtUtc) indexes on DetectedObjects.
/// </summary>
public static class SpeedLogQuery
{
    /// <summary>Sort keys the report's column headers send.</summary>
    public static readonly IReadOnlySet<string> SortKeys = new HashSet<string> { "time", "speed", "camera", "trap", "object" };

    public static async Task<(List<SpeedLogEntry> Rows, int Total)> RunAsync(ApplicationDbContext db, IReadOnlySet<Guid>? accessible,
        int page, int pageSize, string? query, string? sort, bool descending, CancellationToken ct = default)
    {
        if (accessible is { Count: 0 }) return ([], 0);

        var objects = db.DetectedObjects.AsNoTracking().Where(o => o.SpeedKph != null && o.SpeedMeasuredAtUtc != null);
        if (accessible is not null)
        {
            var ids = accessible.ToList();
            objects = objects.Where(o => ids.Contains(o.CameraId));
        }

        var q = from o in objects
                join c in db.Cameras.AsNoTracking() on o.CameraId equals c.Id
                from t in db.SpeedTraps.AsNoTracking().Where(t => t.Id == o.SpeedTrapId).DefaultIfEmpty()
                select new
                {
                    o.Id, At = o.SpeedMeasuredAtUtc!.Value, o.CameraId, CameraName = c.Name,
                    TrapId = o.SpeedTrapId, TrapName = t != null ? t.Name : null, Limit = t != null ? t.SpeedLimitKph : null,
                    o.Label, Color = o.PrimaryColor ?? o.UpperColor, Kph = o.SpeedKph!.Value
                };

        if (!string.IsNullOrWhiteSpace(query))
        {
            var text = query.Trim();
            q = q.Where(r => r.CameraName.Contains(text) || r.Label.Contains(text)
                || (r.TrapName != null && r.TrapName.Contains(text)) || (r.Color != null && r.Color.Contains(text)));
        }

        var total = await q.CountAsync(ct);

        q = (sort, descending) switch
        {
            ("speed", true) => q.OrderByDescending(r => r.Kph).ThenByDescending(r => r.At),
            ("speed", false) => q.OrderBy(r => r.Kph).ThenByDescending(r => r.At),
            ("camera", true) => q.OrderByDescending(r => r.CameraName).ThenByDescending(r => r.At),
            ("camera", false) => q.OrderBy(r => r.CameraName).ThenByDescending(r => r.At),
            ("trap", true) => q.OrderByDescending(r => r.TrapName).ThenByDescending(r => r.At),
            ("trap", false) => q.OrderBy(r => r.TrapName).ThenByDescending(r => r.At),
            ("object", true) => q.OrderByDescending(r => r.Label).ThenByDescending(r => r.At),
            ("object", false) => q.OrderBy(r => r.Label).ThenByDescending(r => r.At),
            ("time", false) => q.OrderBy(r => r.At).ThenBy(r => r.Id),
            _ => q.OrderByDescending(r => r.At).ThenByDescending(r => r.Id),
        };

        var rows = await q.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (rows.Select(r => new SpeedLogEntry(r.Id, r.At, r.CameraId, r.CameraName, r.TrapId,
            r.TrapName ?? (r.TrapId is null ? "" : "(deleted trap)"), r.Label, r.Color, r.Kph, r.Limit)).ToList(), total);
    }
}
