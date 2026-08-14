using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;

namespace LarisVMS.Core.Interfaces;

/// <summary>M8: CRUD for Zone, the one polygon editor covering all four ZoneKinds — see Zone's own
/// doc comment for what each kind actually does as of pass 1.</summary>
public interface IZoneService
{
    Task<List<Zone>> ListAsync(Guid cameraId, CancellationToken ct = default);
    Task<Zone?> GetAsync(Guid id, CancellationToken ct = default);

    Task<Zone> CreateAsync(Guid cameraId, string name, ZoneKind kind, string polygonJson, double sensitivity, CancellationToken ct = default);

    Task UpdateAsync(Guid id, string name, ZoneKind kind, string polygonJson, double sensitivity, bool isEnabled, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
