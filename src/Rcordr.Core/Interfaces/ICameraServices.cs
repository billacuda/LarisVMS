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
        bool isEnabled, long? quotaBytes, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Re-runs GetDeviceInformation/GetCapabilities/GetServices/GetProfiles against a
    /// saved camera and persists the refreshed CameraCapabilities + CameraStream rows.</summary>
    Task<CameraProbeSummary> ProbeAsync(Guid cameraId, CancellationToken ct = default);

    /// <summary>Sum of Segments.SizeBytes per camera — what M4's per-camera quota is checked
    /// against, and what Cameras/Index shows as "in use".</summary>
    Task<Dictionary<Guid, long>> GetStorageUsageAsync(CancellationToken ct = default);

    /// <summary>Toggles a stream's IsEnabled and/or sets its display-name override. A disabled Main
    /// stream stops being handed to nodes (NodeService.GetConfigAsync), so this is how recording on
    /// a specific stream is turned off without touching the camera itself. A blank customName clears
    /// the override back to the default Role-based label.</summary>
    Task UpdateStreamAsync(Guid streamId, bool isEnabled, string? customName, CancellationToken ct = default);
}

public interface ICameraGroupService
{
    Task<List<CameraGroup>> GetTreeAsync(CancellationToken ct = default);
    Task<CameraGroup> CreateAsync(string name, Guid? parentId, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
