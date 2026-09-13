using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Cameras;

[Authorize("Cameras.Edit")]
public class EditModel(ICameraService cameraService, INodeService nodeService,
    ISettingsResolver settings, IZoneService zoneService, IEventTagRuleService eventTagRuleService,
    IScheduleWindowService scheduleWindowService, IAuditService auditService) : PageModel
{
    [BindProperty] public Guid? Id { get; set; }
    [BindProperty] public string Name { get; set; } = string.Empty;
    [BindProperty] public string DeviceServiceUri { get; set; } = string.Empty;
    [BindProperty] public Guid? NodeId { get; set; }
    [BindProperty] public string? Username { get; set; }
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public bool IsEnabled { get; set; } = true;
    [BindProperty] public decimal? QuotaGb { get; set; }
    [BindProperty] public int? RetentionDaysOverride { get; set; }
    /// <summary>Per-camera "archived for longer" — how long this camera's footage stays on the
    /// archive volume, from its original record date. Blank inherits the node/global default.</summary>
    [BindProperty] public int? ArchiveRetentionDaysOverride { get; set; }
    /// <summary>"" (blank) means inherit — same convention as every other override field here, just
    /// expressed as an empty select option instead of a blank number input.</summary>
    [BindProperty] public string RecordingModeOverride { get; set; } = "";
    [BindProperty] public int? MotionPreRollSecondsOverride { get; set; }
    [BindProperty] public int? MotionPostRollSecondsOverride { get; set; }
    [BindProperty] public int? SegmentSecondsOverride { get; set; }
    /// <summary>Object detection plan: whether this camera's node should watch it for AI object
    /// detection — a plain Camera column, not a Settings-table override (see ICameraService.UpdateAsync's
    /// own doc comment for why).</summary>
    [BindProperty] public bool AiDetectionEnabled { get; set; }
    /// <summary>Object detection plan decision 9: "" (blank) means no explicit choice — same
    /// inherit-style convention RecordingModeOverride uses, resolved dynamically node-side by
    /// NodeWorker.ResolvePrimaryMotionSource rather than defaulted here.</summary>
    [BindProperty] public string MotionDetectionSourceOverride { get; set; } = "";
    /// <summary>Detection/hardware-acceleration overhaul, pass 0 — see Camera.ServerMotionEnabled's
    /// own doc comment. A plain Camera column like AiDetectionEnabled above, not a Settings-table
    /// override. Defaults true so a brand-new camera (never loaded from the database) matches the
    /// entity's own default rather than starting unchecked.</summary>
    [BindProperty] public bool ServerMotionEnabled { get; set; } = true;
    [BindProperty] public double? ConfidenceOverride { get; set; }
    [BindProperty] public double? IouOverride { get; set; }
    /// <summary>"" (blank) means inherit — same convention as RecordingModeOverride.</summary>
    [BindProperty] public string AiDetectionStreamRoleOverride { get; set; } = "";
    /// <summary>"" (blank) means inherit, same as AiDetectionStreamRoleOverride above. Corrects a
    /// camera that misreports the shape of its detection stream — see
    /// LarisVMS.Node.DetectionOrientation.</summary>
    [BindProperty] public string AiDetectionOrientationOverride { get; set; } = "";
    /// <summary>Detection.RejectMotionJitter for this camera: "" (blank) = inherit the node/global
    /// default, "True"/"False" = force it. Same inherit convention as AiDetectionStreamRoleOverride;
    /// the values match bool.ToString() so SettingsResolver parses them back.</summary>
    [BindProperty] public string RejectMotionJitterOverride { get; set; } = "";
    /// <summary>Detection.MotionJitterPixels for this camera (1-15), or null to inherit — same
    /// nullable-override shape as ConfidenceOverride.</summary>
    [BindProperty] public int? MotionJitterPixelsOverride { get; set; }
    /// <summary>Detection.MaxFps for this camera, or null to inherit the node/global ceiling — same
    /// nullable-override shape as ConfidenceOverride/MotionJitterPixelsOverride. A camera that only
    /// needs to catch a parked-vehicle alert has no use for the same frame rate a busy street camera
    /// needs, and every frame above what it actually needs is wasted decode/preprocess/inference cost
    /// on this node and, for the external HTTP backend, on the far side of the network too.</summary>
    [BindProperty] public int? MaxFpsOverride { get; set; }

    public bool IsNew => Id is null;

    /// <summary>Read-only — group membership is managed on Cameras/Groups now, not here (this page
    /// used to carry an editable multi-select with its own site-conflict validation surface; moved out
    /// to keep this form to what's actually about the camera itself). Excludes the built-in "All
    /// Cameras" group (CameraGroup.AllCamerasId), same as every other "current groups" display in this
    /// app — it's true for every camera, so listing it here is noise, not information.</summary>
    public List<CameraGroup> CurrentGroups { get; set; } = [];

    public List<Node> Nodes { get; set; } = [];
    public CameraCapabilities? Capabilities { get; set; }
    public List<CameraStream> Streams { get; set; } = [];
    public string? ErrorMessage { get; set; }
    public string? ProbeMessage { get; set; }
    public int EffectiveRetentionDays { get; set; }
    public bool EffectiveArchiveEnabled { get; set; }
    public int EffectiveArchiveRetentionDays { get; set; }
    public string EffectiveRecordingMode { get; set; } = "Continuous";
    public int EffectiveMotionPreRollSeconds { get; set; }
    public int EffectiveMotionPostRollSeconds { get; set; }
    public int EffectiveSegmentSeconds { get; set; }
    public double EffectiveConfidence { get; set; }
    public double EffectiveIou { get; set; }
    public string EffectiveAiDetectionStreamRole { get; set; } = "Sub";
    public string EffectiveAiDetectionOrientation { get; set; } = "Auto";
    public bool EffectiveRejectMotionJitter { get; set; }
    public int EffectiveMotionJitterPixels { get; set; } = 3;
    public int EffectiveMaxFps { get; set; } = 10;
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

    /// <summary>How many distinct physical sensors the last probe found on this device. Only set by
    /// OnPostProbeAsync (a probe is the only thing that reports it); >1 is what offers the
    /// split-into-per-lens-cameras action. Zero on a normal page load, which simply hides the offer
    /// rather than claiming the device has no channels.</summary>
    public int DetectedChannelCount { get; set; }

    /// <summary>Set when this camera is already pinned to one lens of a multi-sensor device — the
    /// page shows which channel it covers instead of re-offering the split.</summary>
    public string? VideoSourceToken { get; set; }

    /// <summary>The vendor plugin auto-detected for this camera's make/model, if any — resolved from
    /// the stored key so the page shows the provider's own name and summary rather than a raw slug.
    /// Null both for cameras that need nothing and for a key no registered provider claims.</summary>
    public ICameraIntegrationProvider? Integration => CameraIntegrations.ByKey(IntegrationKey);

    public string? IntegrationKey { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid? id, string? deviceServiceUri, string? suggestedName)
    {
        Nodes = await nodeService.ListAsync();
        Id = id;

        if (id is not null)
        {
            var camera = await cameraService.GetAsync(id.Value);
            if (camera is null) return RedirectToPage("Index");

            Name = camera.Name;
            DeviceServiceUri = camera.DeviceServiceUri;
            CurrentGroups = camera.Groups.Where(g => g.Id != CameraGroup.AllCamerasId).OrderBy(g => g.Name).ToList();
            NodeId = camera.NodeId;
            IsEnabled = camera.IsEnabled;
            Capabilities = camera.Capabilities;
            Streams = camera.Streams.ToList();
            QuotaGb = camera.QuotaBytes is { } q ? Math.Round(q / 1024m / 1024 / 1024, 2) : null;
            VideoSourceToken = camera.VideoSourceToken;
            IntegrationKey = camera.IntegrationKey;
            AiDetectionEnabled = camera.AiDetectionEnabled;
            MotionDetectionSourceOverride = camera.MotionDetectionSource?.ToString() ?? "";
            ServerMotionEnabled = camera.ServerMotionEnabled;

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

        var ownArchiveRetentionOverride = await settings.GetOwnOverrideAsync(SettingScope.Camera, cameraId, "Archive.RetentionDays");
        ArchiveRetentionDaysOverride = int.TryParse(ownArchiveRetentionOverride, out var ard) ? ard : null;
        EffectiveArchiveEnabled = await settings.GetAsync("Archive.Enabled", false, cameraId: cameraId, nodeId: nodeId);
        EffectiveArchiveRetentionDays = await settings.GetAsync("Archive.RetentionDays", 0, cameraId: cameraId, nodeId: nodeId);

        RecordingModeOverride = await settings.GetOwnOverrideAsync(SettingScope.Camera, cameraId, "Recording.Mode") ?? "";
        EffectiveRecordingMode = await settings.GetAsync("Recording.Mode", "Continuous", cameraId: cameraId, nodeId: nodeId);

        var ownPreRollOverride = await settings.GetOwnOverrideAsync(SettingScope.Camera, cameraId, "Recording.MotionPreRollSeconds");
        MotionPreRollSecondsOverride = int.TryParse(ownPreRollOverride, out var preRoll) ? preRoll : null;
        EffectiveMotionPreRollSeconds = await settings.GetAsync("Recording.MotionPreRollSeconds", 10, cameraId: cameraId, nodeId: nodeId);

        var ownPostRollOverride = await settings.GetOwnOverrideAsync(SettingScope.Camera, cameraId, "Recording.MotionPostRollSeconds");
        MotionPostRollSecondsOverride = int.TryParse(ownPostRollOverride, out var postRoll) ? postRoll : null;
        EffectiveMotionPostRollSeconds = await settings.GetAsync("Recording.MotionPostRollSeconds", 30, cameraId: cameraId, nodeId: nodeId);

        var ownSegmentSecondsOverride = await settings.GetOwnOverrideAsync(SettingScope.Camera, cameraId, "Recording.SegmentSeconds");
        SegmentSecondsOverride = int.TryParse(ownSegmentSecondsOverride, out var segmentSeconds) ? segmentSeconds : null;
        EffectiveSegmentSeconds = await settings.GetAsync("Recording.SegmentSeconds", 60, cameraId: cameraId, nodeId: nodeId);

        var ownConfidenceOverride = await settings.GetOwnOverrideAsync(SettingScope.Camera, cameraId, "Detection.Confidence");
        ConfidenceOverride = double.TryParse(ownConfidenceOverride, out var confidence) ? confidence : null;
        EffectiveConfidence = await settings.GetAsync("Detection.Confidence", 0.35, cameraId: cameraId, nodeId: nodeId);

        var ownIouOverride = await settings.GetOwnOverrideAsync(SettingScope.Camera, cameraId, "Detection.Iou");
        IouOverride = double.TryParse(ownIouOverride, out var iou) ? iou : null;
        EffectiveIou = await settings.GetAsync("Detection.Iou", 0.5, cameraId: cameraId, nodeId: nodeId);

        AiDetectionStreamRoleOverride = await settings.GetOwnOverrideAsync(SettingScope.Camera, cameraId, "AiDetection.StreamRole") ?? "";
        EffectiveAiDetectionStreamRole = await settings.GetAsync("AiDetection.StreamRole", "Sub", cameraId: cameraId, nodeId: nodeId);
        AiDetectionOrientationOverride = await settings.GetOwnOverrideAsync(SettingScope.Camera, cameraId, "AiDetection.Orientation") ?? "";
        EffectiveAiDetectionOrientation = await settings.GetAsync("AiDetection.Orientation", "Auto", cameraId: cameraId, nodeId: nodeId);

        RejectMotionJitterOverride =
            await settings.GetOwnOverrideAsync(SettingScope.Camera, cameraId, "Detection.RejectMotionJitter") is { } ownJitter
            && bool.TryParse(ownJitter, out var jitterOverride) ? jitterOverride.ToString() : "";
        EffectiveRejectMotionJitter = await settings.GetAsync("Detection.RejectMotionJitter", false, cameraId: cameraId, nodeId: nodeId);
        var ownJitterPixels = await settings.GetOwnOverrideAsync(SettingScope.Camera, cameraId, "Detection.MotionJitterPixels");
        MotionJitterPixelsOverride = int.TryParse(ownJitterPixels, out var jitterPixels) ? jitterPixels : null;
        EffectiveMotionJitterPixels = await settings.GetAsync("Detection.MotionJitterPixels", 3, cameraId: cameraId, nodeId: nodeId);

        var ownMaxFps = await settings.GetOwnOverrideAsync(SettingScope.Camera, cameraId, "Detection.MaxFps");
        MaxFpsOverride = int.TryParse(ownMaxFps, out var maxFps) ? maxFps : null;
        EffectiveMaxFps = await settings.GetAsync("Detection.MaxFps", 10, cameraId: cameraId, nodeId: nodeId);

        var zones = await zoneService.ListAsync(cameraId);
        HasServerMotionZone = zones.Any(z => z.Kind == ZoneKind.ServerMotion && z.IsEnabled);

        var eventTagRules = await eventTagRuleService.ListAsync(cameraId);
        HasDrivingEventRule = eventTagRules.Any(r => r.DrivesRecording && r.IsEnabled);

        var scheduleWindows = await scheduleWindowService.ListAsync(cameraId);
        HasScheduleWindow = scheduleWindows.Any(w => w.IsEnabled);
    }

    /// <summary>Named, not the unnamed OnPostAsync: the form has no explicit action, so a handler-less
    /// Save posts to the current document URL — which still reads "?handler=Probe" after Re-probe
    /// (that handler returns Page(), it doesn't redirect). Save would then silently re-probe instead
    /// of writing. Same fix as Admin/Settings/Detection's own Save.</summary>
    public async Task<IActionResult> OnPostSaveAsync()
    {
        Nodes = await nodeService.ListAsync();

        try
        {
            if (Id is null)
            {
                // Stays on Edit (rather than Index) so the auto-probe this triggers — capabilities,
                // streams — is immediately visible; that feedback matters most right when a camera
                // is first added. No initial group here — group membership is set up afterward on
                // Cameras/Groups; every camera picks up the built-in "All Cameras" group regardless
                // (CameraService.AddAsync).
                var camera = await cameraService.AddAsync(new AddCameraRequest(Name, DeviceServiceUri, Username, Password,
                    GroupId: null));
                if (NodeId is not null)
                    await nodeService.AssignCameraAsync(camera.Id, NodeId);
                await LogAsync("Camera.Create", $"{Name} ({camera.Id})");
                return RedirectToPage("Edit", new { id = camera.Id });
            }

            var quotaBytes = QuotaGb is { } gb ? (long)(gb * 1024 * 1024 * 1024) : (long?)null;

            // Read the pre-edit state first so the audit entry can say what actually changed rather
            // than only naming the camera. Credentials are reported via SecretChanged rather than a
            // real before/after comparison: GetAsync deliberately never projects them (see
            // ProjectWithoutCredentials), and a blank submission means "leave unchanged" — so a
            // non-blank submission is the only signal available, and the value itself must never
            // reach the log regardless.
            var before = await cameraService.GetAsync(Id.Value);
            var oldRetentionOverride = await settings.GetOwnOverrideAsync(SettingScope.Camera, Id.Value, "Retention.Days");
            var oldArchiveRetentionOverride = await settings.GetOwnOverrideAsync(SettingScope.Camera, Id.Value, "Archive.RetentionDays");

            var motionDetectionSource = Enum.TryParse<MotionDetectionSource>(MotionDetectionSourceOverride, out var mds) ? mds : (MotionDetectionSource?)null;

            await cameraService.UpdateAsync(Id.Value, Name, NodeId, Username, Password, IsEnabled, quotaBytes,
                DeviceServiceUri, AiDetectionEnabled, motionDetectionSource, ServerMotionEnabled);

            var details = AuditDiff.Build(
                AuditDiff.Of("Name", before?.Name, Name),
                AuditDiff.Of("Device service URL", before?.DeviceServiceUri, DeviceServiceUri),
                AuditDiff.Of("Node", NodeName(before?.NodeId), NodeName(NodeId)),
                AuditDiff.Of("Enabled", before?.IsEnabled.ToString(), IsEnabled.ToString()),
                AuditDiff.Of("Quota", QuotaText(before?.QuotaBytes), QuotaText(quotaBytes)),
                AuditDiff.Of("Retention override", oldRetentionOverride, RetentionDaysOverride?.ToString()),
                AuditDiff.Of("Archive retention override", oldArchiveRetentionOverride, ArchiveRetentionDaysOverride?.ToString()),
                AuditDiff.Of("AI detection enabled", before?.AiDetectionEnabled.ToString(), AiDetectionEnabled.ToString()),
                AuditDiff.Of("Motion detection source", before?.MotionDetectionSource?.ToString(), motionDetectionSource?.ToString()),
                AuditDiff.Of("Server-side motion detection enabled", before?.ServerMotionEnabled.ToString(), ServerMotionEnabled.ToString()),
                AuditDiff.SecretChanged("Username", !string.IsNullOrWhiteSpace(Username)),
                AuditDiff.SecretChanged("Password", !string.IsNullOrWhiteSpace(Password)));

            await LogAsync("Camera.Update", details is null ? $"{Name} ({Id})" : $"{Name} ({Id}) — {details}");
            await settings.SetOverrideAsync(SettingScope.Camera, Id.Value, "Retention.Days",
                RetentionDaysOverride?.ToString(), User.Identity?.Name);
            await settings.SetOverrideAsync(SettingScope.Camera, Id.Value, "Archive.RetentionDays",
                ArchiveRetentionDaysOverride?.ToString(), User.Identity?.Name);
            await settings.SetOverrideAsync(SettingScope.Camera, Id.Value, "Recording.Mode",
                string.IsNullOrEmpty(RecordingModeOverride) ? null : RecordingModeOverride, User.Identity?.Name);
            await settings.SetOverrideAsync(SettingScope.Camera, Id.Value, "Recording.MotionPreRollSeconds",
                MotionPreRollSecondsOverride?.ToString(), User.Identity?.Name);
            await settings.SetOverrideAsync(SettingScope.Camera, Id.Value, "Recording.MotionPostRollSeconds",
                MotionPostRollSecondsOverride?.ToString(), User.Identity?.Name);
            // Same clamp NodeService.GetConfigAsync applies when resolving this for a node.
            var clampedSegmentSecondsOverride = SegmentSecondsOverride is { } s ? Math.Clamp(s, 5, 300) : (int?)null;
            await settings.SetOverrideAsync(SettingScope.Camera, Id.Value, "Recording.SegmentSeconds",
                clampedSegmentSecondsOverride?.ToString(), User.Identity?.Name);

            // Same 0-1 clamp Admin/Settings/Detection applies to the global default.
            var clampedConfidenceOverride = ConfidenceOverride is { } conf ? Math.Clamp(conf, 0, 1) : (double?)null;
            await settings.SetOverrideAsync(SettingScope.Camera, Id.Value, "Detection.Confidence",
                clampedConfidenceOverride?.ToString("0.####"), User.Identity?.Name);
            var clampedIouOverride = IouOverride is { } io ? Math.Clamp(io, 0, 1) : (double?)null;
            await settings.SetOverrideAsync(SettingScope.Camera, Id.Value, "Detection.Iou",
                clampedIouOverride?.ToString("0.####"), User.Identity?.Name);
            await settings.SetOverrideAsync(SettingScope.Camera, Id.Value, "AiDetection.StreamRole",
                string.IsNullOrEmpty(AiDetectionStreamRoleOverride) ? null : AiDetectionStreamRoleOverride, User.Identity?.Name);
            await settings.SetOverrideAsync(SettingScope.Camera, Id.Value, "AiDetection.Orientation",
                string.IsNullOrEmpty(AiDetectionOrientationOverride) ? null : AiDetectionOrientationOverride, User.Identity?.Name);
            await settings.SetOverrideAsync(SettingScope.Camera, Id.Value, "Detection.RejectMotionJitter",
                string.IsNullOrEmpty(RejectMotionJitterOverride) ? null : RejectMotionJitterOverride, User.Identity?.Name);
            // Same 1-15 clamp NodeService.GetConfigAsync applies when resolving this for a node.
            var clampedJitterPixelsOverride = MotionJitterPixelsOverride is { } jp ? Math.Clamp(jp, 1, 15) : (int?)null;
            await settings.SetOverrideAsync(SettingScope.Camera, Id.Value, "Detection.MotionJitterPixels",
                clampedJitterPixelsOverride?.ToString(), User.Identity?.Name);
            // Same 0-60 clamp Admin/Settings/Detection applies to the global default; 0 = no cap.
            var clampedMaxFpsOverride = MaxFpsOverride is { } mf ? Math.Clamp(mf, 0, 60) : (int?)null;
            await settings.SetOverrideAsync(SettingScope.Camera, Id.Value, "Detection.MaxFps",
                clampedMaxFpsOverride?.ToString(), User.Identity?.Name);
            return RedirectToPage("Index");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return Page();
        }
    }

    public async Task<IActionResult> OnPostSplitChannelsAsync()
    {
        if (Id is null) return RedirectToPage("Index");

        try
        {
            var created = await cameraService.SplitChannelsAsync(Id.Value);
            await LogAsync("Camera.SplitChannels", $"{Name} ({Id}) — {created} channel camera(s) added");
            ProbeMessage = created == 0
                ? "No additional channels to add — every channel on this device already has a camera."
                : $"Added {created} camera(s), one per additional channel on this device.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        return RedirectToPage("Edit", new { id = Id });
    }

    public async Task<IActionResult> OnPostProbeAsync()
    {
        if (Id is null) return RedirectToPage("Index");

        var summary = await cameraService.ProbeAsync(Id.Value);
        ProbeMessage = summary.Error is null
            ? $"Probed successfully — {summary.StreamCount} stream(s) found."
            : $"Probe failed: {summary.Error}";

        // Only meaningful right after a probe, which is the only thing that reports the device's
        // channel list — the page's normal GET has no probe result to read it from.
        DetectedChannelCount = summary.VideoSourceTokens?.Count ?? 0;

        var camera = await cameraService.GetAsync(Id.Value);
        if (camera is not null)
        {
            Name = camera.Name;
            DeviceServiceUri = camera.DeviceServiceUri;
            CurrentGroups = camera.Groups.Where(g => g.Id != CameraGroup.AllCamerasId).OrderBy(g => g.Name).ToList();
            NodeId = camera.NodeId;
            IsEnabled = camera.IsEnabled;
            Capabilities = camera.Capabilities;
            Streams = camera.Streams.ToList();
            QuotaGb = camera.QuotaBytes is { } q ? Math.Round(q / 1024m / 1024 / 1024, 2) : null;
            VideoSourceToken = camera.VideoSourceToken;
            IntegrationKey = camera.IntegrationKey;
            AiDetectionEnabled = camera.AiDetectionEnabled;
            MotionDetectionSourceOverride = camera.MotionDetectionSource?.ToString() ?? "";
            ServerMotionEnabled = camera.ServerMotionEnabled;

            await LoadEffectiveSettingsAsync(Id.Value, camera.NodeId);
            StorageUsedBytes = (await cameraService.GetStorageUsageAsync()).GetValueOrDefault(Id.Value);
        }
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

    // A name rather than a raw GUID in the audit trail — "Node: NVR1 → WINSERV1" is readable months
    // later in a way two opaque ids never are. Already loaded by OnPostAsync, so this costs no extra
    // query. Falls back to the id if the referenced row is gone (deleted between the edit being loaded
    // and submitted), which is still better than logging nothing.
    private string NodeName(Guid? nodeId) => nodeId is null
        ? string.Empty
        : Nodes.FirstOrDefault(n => n.Id == nodeId)?.Name ?? nodeId.ToString()!;

    private static string QuotaText(long? quotaBytes) => quotaBytes is { } bytes
        ? $"{Math.Round(bytes / 1024m / 1024 / 1024, 2)} GB"
        : string.Empty;

    public async Task<IActionResult> OnPostUpdateStreamAsync(Guid streamId, bool streamIsEnabled, string? customName)
    {
        await cameraService.UpdateStreamAsync(streamId, streamIsEnabled, customName);
        return RedirectToPage("Edit", new { id = Id });
    }
}
