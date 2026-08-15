using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Cameras;

[Authorize("Cameras.Edit")]
public class EditModel(ICameraService cameraService, ICameraGroupService groupService, INodeService nodeService,
    ISettingsResolver settings, IZoneService zoneService, IEventTagRuleService eventTagRuleService,
    IScheduleWindowService scheduleWindowService, IAuditService auditService) : PageModel
{
    [BindProperty] public Guid? Id { get; set; }
    [BindProperty] public string Name { get; set; } = string.Empty;
    [BindProperty] public string DeviceServiceUri { get; set; } = string.Empty;
    [BindProperty] public Guid? GroupId { get; set; }
    [BindProperty] public Guid? NodeId { get; set; }
    [BindProperty] public string? Username { get; set; }
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public bool IsEnabled { get; set; } = true;
    [BindProperty] public decimal? QuotaGb { get; set; }
    [BindProperty] public int? RetentionDaysOverride { get; set; }
    /// <summary>"" (blank) means inherit — same convention as every other override field here, just
    /// expressed as an empty select option instead of a blank number input.</summary>
    [BindProperty] public string RecordingModeOverride { get; set; } = "";
    [BindProperty] public int? MotionPreRollSecondsOverride { get; set; }
    [BindProperty] public int? MotionPostRollSecondsOverride { get; set; }

    public bool IsNew => Id is null;
    public List<CameraGroup> Groups { get; set; } = [];
    public List<Node> Nodes { get; set; } = [];
    public CameraCapabilities? Capabilities { get; set; }
    public List<CameraStream> Streams { get; set; } = [];
    public string? ErrorMessage { get; set; }
    public string? ProbeMessage { get; set; }
    public int EffectiveRetentionDays { get; set; }
    public string EffectiveRecordingMode { get; set; } = "Continuous";
    public int EffectiveMotionPreRollSeconds { get; set; }
    public int EffectiveMotionPostRollSeconds { get; set; }
    /// <summary>Whether this camera has at least one enabled ServerMotion zone — Motion mode does
    /// nothing without one (NodeWorker falls back to recording everything, logging a warning) so
    /// the Edit page can surface that up front instead of the operator discovering it in node logs.</summary>
    public bool HasServerMotionZone { get; set; }
    /// <summary>Same "surface the gap up front" reasoning as HasServerMotionZone, for Event mode:
    /// it does nothing without at least one enabled EventTagRule with DrivesRecording checked.</summary>
    public bool HasDrivingEventRule { get; set; }
    /// <summary>Same reasoning again, for Schedule mode: it fails open (keeps everything) with no
    /// window configured, same as the other two gaps above.</summary>
    public bool HasScheduleWindow { get; set; }
    public long StorageUsedBytes { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid? id, string? deviceServiceUri, string? suggestedName)
    {
        Groups = await groupService.GetTreeAsync();
        Nodes = await nodeService.ListAsync();
        Id = id;

        if (id is not null)
        {
            var camera = await cameraService.GetAsync(id.Value);
            if (camera is null) return RedirectToPage("Index");

            Name = camera.Name;
            DeviceServiceUri = camera.DeviceServiceUri;
            GroupId = camera.GroupId;
            NodeId = camera.NodeId;
            IsEnabled = camera.IsEnabled;
            Capabilities = camera.Capabilities;
            Streams = camera.Streams.ToList();
            QuotaGb = camera.QuotaBytes is { } q ? Math.Round(q / 1024m / 1024 / 1024, 2) : null;

            await LoadEffectiveSettingsAsync(id.Value, camera.NodeId);
            StorageUsedBytes = (await cameraService.GetStorageUsageAsync()).GetValueOrDefault(id.Value);
        }
        else
        {
            DeviceServiceUri = deviceServiceUri ?? string.Empty;
            Name = suggestedName ?? string.Empty;
        }

        return Page();
    }

    /// <summary>Retention + M8 recording-mode fields (Recording.Mode, MotionPreRollSeconds,
    /// MotionPostRollSeconds) — the "own override" + "effective resolved value" pair every
    /// settings-backed field on this page follows, loaded identically in OnGetAsync and
    /// OnPostProbeAsync (both need to show the same picture after their respective actions), so
    /// it's factored here rather than duplicated a third time.</summary>
    private async Task LoadEffectiveSettingsAsync(Guid cameraId, Guid? nodeId)
    {
        var ownRetentionOverride = await settings.GetOwnOverrideAsync(SettingScope.Camera, cameraId, "Retention.Days");
        RetentionDaysOverride = int.TryParse(ownRetentionOverride, out var days) ? days : null;
        EffectiveRetentionDays = await settings.GetAsync("Retention.Days", 30, cameraId: cameraId, nodeId: nodeId);

        RecordingModeOverride = await settings.GetOwnOverrideAsync(SettingScope.Camera, cameraId, "Recording.Mode") ?? "";
        EffectiveRecordingMode = await settings.GetAsync("Recording.Mode", "Continuous", cameraId: cameraId, nodeId: nodeId);

        var ownPreRollOverride = await settings.GetOwnOverrideAsync(SettingScope.Camera, cameraId, "Recording.MotionPreRollSeconds");
        MotionPreRollSecondsOverride = int.TryParse(ownPreRollOverride, out var preRoll) ? preRoll : null;
        EffectiveMotionPreRollSeconds = await settings.GetAsync("Recording.MotionPreRollSeconds", 10, cameraId: cameraId, nodeId: nodeId);

        var ownPostRollOverride = await settings.GetOwnOverrideAsync(SettingScope.Camera, cameraId, "Recording.MotionPostRollSeconds");
        MotionPostRollSecondsOverride = int.TryParse(ownPostRollOverride, out var postRoll) ? postRoll : null;
        EffectiveMotionPostRollSeconds = await settings.GetAsync("Recording.MotionPostRollSeconds", 30, cameraId: cameraId, nodeId: nodeId);

        var zones = await zoneService.ListAsync(cameraId);
        HasServerMotionZone = zones.Any(z => z.Kind == ZoneKind.ServerMotion && z.IsEnabled);

        var eventTagRules = await eventTagRuleService.ListAsync(cameraId);
        HasDrivingEventRule = eventTagRules.Any(r => r.DrivesRecording && r.IsEnabled);

        var scheduleWindows = await scheduleWindowService.ListAsync(cameraId);
        HasScheduleWindow = scheduleWindows.Any(w => w.IsEnabled);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Groups = await groupService.GetTreeAsync();
        Nodes = await nodeService.ListAsync();

        try
        {
            if (Id is null)
            {
                // Stays on Edit (rather than Index) so the auto-probe this triggers — capabilities,
                // streams — is immediately visible; that feedback matters most right when a camera
                // is first added.
                var camera = await cameraService.AddAsync(new AddCameraRequest(Name, DeviceServiceUri, Username, Password, GroupId));
                if (NodeId is not null)
                    await nodeService.AssignCameraAsync(camera.Id, NodeId);
                await LogAsync("Camera.Create", $"{Name} ({camera.Id})");
                return RedirectToPage("Edit", new { id = camera.Id });
            }

            var quotaBytes = QuotaGb is { } gb ? (long)(gb * 1024 * 1024 * 1024) : (long?)null;
            await cameraService.UpdateAsync(Id.Value, Name, GroupId, NodeId, Username, Password, IsEnabled, quotaBytes);
            await LogAsync("Camera.Update", $"{Name} ({Id})");
            await settings.SetOverrideAsync(SettingScope.Camera, Id.Value, "Retention.Days",
                RetentionDaysOverride?.ToString(), User.Identity?.Name);
            await settings.SetOverrideAsync(SettingScope.Camera, Id.Value, "Recording.Mode",
                string.IsNullOrEmpty(RecordingModeOverride) ? null : RecordingModeOverride, User.Identity?.Name);
            await settings.SetOverrideAsync(SettingScope.Camera, Id.Value, "Recording.MotionPreRollSeconds",
                MotionPreRollSecondsOverride?.ToString(), User.Identity?.Name);
            await settings.SetOverrideAsync(SettingScope.Camera, Id.Value, "Recording.MotionPostRollSeconds",
                MotionPostRollSecondsOverride?.ToString(), User.Identity?.Name);
            return RedirectToPage("Index");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return Page();
        }
    }

    public async Task<IActionResult> OnPostProbeAsync()
    {
        if (Id is null) return RedirectToPage("Index");

        var summary = await cameraService.ProbeAsync(Id.Value);
        ProbeMessage = summary.Error is null
            ? $"Probed successfully — {summary.StreamCount} stream(s) found."
            : $"Probe failed: {summary.Error}";

        var camera = await cameraService.GetAsync(Id.Value);
        if (camera is not null)
        {
            Name = camera.Name;
            DeviceServiceUri = camera.DeviceServiceUri;
            GroupId = camera.GroupId;
            NodeId = camera.NodeId;
            IsEnabled = camera.IsEnabled;
            Capabilities = camera.Capabilities;
            Streams = camera.Streams.ToList();
            QuotaGb = camera.QuotaBytes is { } q ? Math.Round(q / 1024m / 1024 / 1024, 2) : null;

            await LoadEffectiveSettingsAsync(Id.Value, camera.NodeId);
            StorageUsedBytes = (await cameraService.GetStorageUsageAsync()).GetValueOrDefault(Id.Value);
        }
        Groups = await groupService.GetTreeAsync();
        Nodes = await nodeService.ListAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (Id is not null)
        {
            var camera = await cameraService.GetAsync(Id.Value);
            await cameraService.DeleteAsync(Id.Value);
            await LogAsync("Camera.Delete", $"{camera?.Name ?? "?"} ({Id})");
        }
        return RedirectToPage("Index");
    }

    private Task LogAsync(string action, string details) =>
        auditService.LogAsync(action, User.Identity is { IsAuthenticated: true }
            ? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value : null,
            User.Identity?.Name, HttpContext.Connection.RemoteIpAddress?.ToString(), details);

    public async Task<IActionResult> OnPostUpdateStreamAsync(Guid streamId, bool streamIsEnabled, string? customName)
    {
        await cameraService.UpdateStreamAsync(streamId, streamIsEnabled, customName);
        return RedirectToPage("Edit", new { id = Id });
    }
}
