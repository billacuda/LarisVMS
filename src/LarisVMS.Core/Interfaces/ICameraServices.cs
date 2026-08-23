using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;

namespace LarisVMS.Core.Interfaces;

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

    /// <summary><paramref name="deviceServiceUri"/> null or blank means unchanged, the same
    /// convention <paramref name="username"/>/<paramref name="password"/> already use — the edit
    /// form always submits the camera's current address, so this only matters to a caller that
    /// deliberately omits it. A real change (most commonly http &lt;-&gt; https) re-derives
    /// Host/OnvifPort from the new URI, exactly as AddAsync does, and triggers a re-probe since the
    /// device's own capability report can otherwise go stale against the new address. Group
    /// membership isn't a parameter here — see SetCameraGroupsAsync, its own separate concern since a
    /// camera can belong to any number of groups.</summary>
    Task UpdateAsync(Guid id, string name, Guid? nodeId, string? username, string? password,
        bool isEnabled, long? quotaBytes, string? deviceServiceUri = null, CancellationToken ct = default);

    /// <summary>Replaces a camera's entire group membership with exactly the given set — not an
    /// incremental add/remove, so a caller changing only one group's membership (e.g. Groups.cshtml's
    /// per-group camera picker) must compute the camera's full desired set first. Throws
    /// InvalidOperationException if the given groups don't all share one top-level Site
    /// (CameraGroupPolicy.AllShareOneSite) — a camera belongs to exactly one Site at a time.</summary>
    Task SetCameraGroupsAsync(Guid cameraId, IReadOnlyList<Guid> groupIds, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Re-runs GetDeviceInformation/GetCapabilities/GetServices/GetProfiles against a
    /// saved camera and persists the refreshed CameraCapabilities + CameraStream rows.</summary>
    Task<CameraProbeSummary> ProbeAsync(Guid cameraId, CancellationToken ct = default);

    /// <summary>Splits a multi-sensor device (an Axis quad-lens and similar) into one Camera row per
    /// physical lens, so each records independently and can be placed in a view on its own. The
    /// camera passed in claims the first channel; a sibling row sharing its
    /// Host/DeviceServiceUri/credentials/node is created for each remaining one, and every row is
    /// re-probed so it resolves only its own lens's streams. Returns how many new cameras were
    /// created — 0 when the device reports fewer than two sensors, or when every channel already has
    /// a Camera row (so running it twice is safe rather than duplicating).</summary>
    Task<int> SplitChannelsAsync(Guid cameraId, CancellationToken ct = default);

    /// <summary>Sum of Segments.SizeBytes per camera — what M4's per-camera quota is checked
    /// against, and what Cameras/Index shows as "in use".</summary>
    Task<Dictionary<Guid, long>> GetStorageUsageAsync(CancellationToken ct = default);

    /// <summary>Toggles a stream's IsEnabled and/or sets its display-name override. A disabled Main
    /// stream stops being handed to nodes (NodeService.GetConfigAsync), so this is how recording on
    /// a specific stream is turned off without touching the camera itself. A blank customName clears
    /// the override back to the default Role-based label.</summary>
    Task UpdateStreamAsync(Guid streamId, bool isEnabled, string? customName, CancellationToken ct = default);

    /// <summary>Flips Camera.IsEnabled in one round trip (SQL-side NOT, no read-modify-write race) —
    /// the fast path for "stop this one camera without opening Edit" on Cameras/Index.</summary>
    Task ToggleEnabledAsync(Guid id, CancellationToken ct = default);

    /// <summary>Cameras with Segment rows recorded under a NodeId other than their current
    /// Camera.NodeId — footage still sitting on a node the camera is no longer assigned to. Keyed
    /// by CameraId, each value is the distinct set of stale NodeIds that footage sits on. A live
    /// query, not a stored flag: once those rows are gone (retention sweep or manual delete), a
    /// camera drops out on its own with no explicit "clear" step.</summary>
    Task<Dictionary<Guid, List<Guid>>> GetStaleSegmentNodeIdsAsync(CancellationToken ct = default);

    /// <summary>Same underlying stale (camera, node) pairs as GetStaleSegmentNodeIdsAsync, with the
    /// camera name and newest-still-there timestamp attached — see StaleSegmentDetail's own doc
    /// comment for why the newest, not oldest, is what determines when the warning clears.</summary>
    Task<List<StaleSegmentDetail>> GetStaleSegmentDetailsAsync(CancellationToken ct = default);
}

public interface ICameraGroupService
{
    Task<List<CameraGroup>> GetTreeAsync(CancellationToken ct = default);
    Task<CameraGroup> CreateAsync(string name, Guid? parentId, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
