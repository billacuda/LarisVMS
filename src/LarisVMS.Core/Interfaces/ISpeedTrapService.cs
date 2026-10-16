using LarisVMS.Core.Entities;

namespace LarisVMS.Core.Interfaces;

public interface ISpeedTrapService
{
    Task<List<SpeedTrap>> ListAsync(Guid cameraId, CancellationToken ct = default);
    Task<SpeedTrap?> GetAsync(Guid id, CancellationToken ct = default);
    Task<SpeedTrap> CreateAsync(Guid cameraId, string name, string gatesJson, double distanceMeters, double? speedLimitKph = null, CancellationToken ct = default);
    Task UpdateAsync(Guid id, string name, string gatesJson, double distanceMeters, bool isEnabled, double? speedLimitKph = null, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
