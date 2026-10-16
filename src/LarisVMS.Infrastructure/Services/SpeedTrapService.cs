using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

/// <summary>Speed trap CRUD. Validation (gate shape, distance) happens at the API with
/// SpeedTrapGates.Validate; this only stores.</summary>
public class SpeedTrapService(ApplicationDbContext db) : ISpeedTrapService
{
    public async Task<List<SpeedTrap>> ListAsync(Guid cameraId, CancellationToken ct = default)
        => await db.SpeedTraps.AsNoTracking().Where(t => t.CameraId == cameraId).OrderBy(t => t.Name).ToListAsync(ct);

    public async Task<SpeedTrap?> GetAsync(Guid id, CancellationToken ct = default)
        => await db.SpeedTraps.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<SpeedTrap> CreateAsync(Guid cameraId, string name, string gatesJson, double distanceMeters, double? speedLimitKph = null, CancellationToken ct = default)
    {
        var trap = new SpeedTrap
        {
            Id = Guid.NewGuid(),
            CameraId = cameraId,
            Name = name,
            GatesJson = gatesJson,
            DistanceMeters = distanceMeters,
            SpeedLimitKph = speedLimitKph
        };
        db.SpeedTraps.Add(trap);
        await db.SaveChangesAsync(ct);
        return trap;
    }

    public async Task UpdateAsync(Guid id, string name, string gatesJson, double distanceMeters, bool isEnabled, double? speedLimitKph = null, CancellationToken ct = default)
    {
        var trap = await db.SpeedTraps.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new InvalidOperationException("Speed trap not found.");
        trap.Name = name;
        trap.GatesJson = gatesJson;
        trap.DistanceMeters = distanceMeters;
        trap.IsEnabled = isEnabled;
        trap.SpeedLimitKph = speedLimitKph;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var trap = await db.SpeedTraps.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (trap is null) return;
        db.SpeedTraps.Remove(trap);
        await db.SaveChangesAsync(ct);
    }
}
