using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

public class ZoneService(ApplicationDbContext db) : IZoneService
{
    public async Task<List<Zone>> ListAsync(Guid cameraId, CancellationToken ct = default)
        => await db.Zones.AsNoTracking().Where(z => z.CameraId == cameraId).OrderBy(z => z.Name).ToListAsync(ct);

    public async Task<Zone?> GetAsync(Guid id, CancellationToken ct = default)
        => await db.Zones.AsNoTracking().FirstOrDefaultAsync(z => z.Id == id, ct);

    public async Task<Zone> CreateAsync(Guid cameraId, string name, ZoneKind kind, string polygonJson, double sensitivity, CancellationToken ct = default)
    {
        var zone = new Zone
        {
            Id = Guid.NewGuid(),
            CameraId = cameraId,
            Name = name,
            Kind = kind,
            PolygonJson = polygonJson,
            Sensitivity = sensitivity
        };
        db.Zones.Add(zone);
        await db.SaveChangesAsync(ct);
        return zone;
    }

    public async Task UpdateAsync(Guid id, string name, ZoneKind kind, string polygonJson, double sensitivity, bool isEnabled, CancellationToken ct = default)
    {
        var zone = await db.Zones.FirstOrDefaultAsync(z => z.Id == id, ct)
            ?? throw new InvalidOperationException("Zone not found.");
        zone.Name = name;
        zone.Kind = kind;
        zone.PolygonJson = polygonJson;
        zone.Sensitivity = sensitivity;
        zone.IsEnabled = isEnabled;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        // MotionSpans.ZoneId -> Zones is DeleteBehavior.Restrict at the DB level (SQL Server
        // rejects SetNull here — see ApplicationDbContext's comment on that FK), so the null-out
        // that behavior would normally do is done here instead: deleting a zone keeps the motion
        // history it already recorded, it just stops being attributed to a specific zone.
        await db.MotionSpans.Where(m => m.ZoneId == id).ExecuteUpdateAsync(u => u.SetProperty(m => m.ZoneId, (Guid?)null), ct);
        await db.Zones.Where(z => z.Id == id).ExecuteDeleteAsync(ct);
    }
}
