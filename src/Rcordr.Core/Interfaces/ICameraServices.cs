using Rcordr.Core.Dtos;
using Rcordr.Core.Entities;

namespace Rcordr.Core.Interfaces;

public interface ICameraDiscoveryService
{
    Task<IReadOnlyList<DiscoveredCameraDto>> DiscoverAsync(TimeSpan timeout, CancellationToken ct = default);
}

public interface ICameraService
{
    Task<List<Camera>> ListAsync(CancellationToken ct = default);
    Task<Camera?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Creates the Camera row (credentials encrypted at rest) and immediately probes it —
    /// a camera that can't be reached with the credentials given fails fast here rather than sitting
    /// silently un-probed in the list.</summary>
    Task<Camera> AddAsync(AddCameraRequest request, CancellationToken ct = default);

    Task UpdateAsync(Guid id, string name, Guid? groupId, Guid? nodeId, string? username, string? password,
        bool isEnabled, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Re-runs GetDeviceInformation/GetCapabilities/GetServices/GetProfiles against a
    /// saved camera and persists the refreshed CameraCapabilities + CameraStream rows.</summary>
    Task<CameraProbeSummary> ProbeAsync(Guid cameraId, CancellationToken ct = default);
}

public interface ICameraGroupService
{
    Task<List<CameraGroup>> GetTreeAsync(CancellationToken ct = default);
    Task<CameraGroup> CreateAsync(string name, Guid? parentId, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
