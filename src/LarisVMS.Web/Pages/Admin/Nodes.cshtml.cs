using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Core.Security;

namespace LarisVMS.Web.Pages.Admin;

// Razor Pages [Authorize] only applies at the page/model level, not per-handler (MVC1001) — this
// page mixes viewing and editing inline (unlike Cameras, which splits Index/Edit into separate
// pages+policies), so the whole page requires edit access rather than only the POST handlers.
[Authorize("Nodes.Edit")]
public class NodesModel(INodeService nodeService, ICameraService cameraService, ISettingsResolver settings,
    IAuditService auditService, IHttpClientFactory httpFactory, LarisVMS.Web.Services.MediaRelayMetrics relayMetrics,
    IProxyService proxyService, LarisVMS.Web.Services.NodeControlClient nodeControlClient) : PageModel
{
    /// <summary>Failover plan phase 1: the streaming-relay counters, surfaced as a small diagnostics
    /// line — with direct streaming on these trend to zero.</summary>
    public LarisVMS.Web.Services.MediaRelayMetrics RelayMetrics => relayMetrics;

    /// <summary>Failover plan phase 2: proxies an admin can assign as a node's primary/backup.</summary>
    public List<LarisVMS.Core.Entities.MediaProxy> AvailableProxies { get; set; } = [];

    public List<Node> Nodes { get; set; } = [];

    /// <summary>Survives the redirect after a restart POST so the result is visible on the reloaded
    /// page — TempData rather than a property, since RedirectToPage builds a fresh model.</summary>
    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public bool StatusIsError { get; set; }
    public Dictionary<Guid, int> CameraCountByNode { get; set; } = [];
    public Dictionary<Guid, int> EffectiveRetentionDays { get; set; } = [];
    public Dictionary<Guid, int?> RetentionOverride { get; set; } = [];
    /// <summary>"" (blank) means inherit the global default — same convention RetentionOverride's
    /// own blank-means-null uses, just expressed as a select option instead of a blank number
    /// input. See NodeConfigResponse.DetectionModelFamily's own doc comment for why this is a
    /// per-node override, not per-camera.</summary>
    public Dictionary<Guid, string> ModelFamilyOverride { get; set; } = [];
    public Dictionary<Guid, string> DFineWeightsOverride { get; set; } = [];
    public Dictionary<Guid, string> YoloXSizeOverride { get; set; } = [];
    /// <summary>Detection.LocalModelName per-node override — only acted on when ModelFamilyOverride
    /// (or the inherited default) resolves to "Custom". Same per-node override shape as the fields
    /// above; free-typed rather than a hard-constrained select since the model that name refers to
    /// lives on the node itself, not something this page's own GET can enumerate without probing it
    /// (see the "Refresh models" button / OnGetLocalModelsAsync).</summary>
    public Dictionary<Guid, string> LocalModelNameOverride { get; set; } = [];
    public Dictionary<Guid, int?> MaxFpsOverride { get; set; } = [];
    public Dictionary<Guid, string> EffectiveModelFamily { get; set; } = [];
    /// <summary>Detection/hardware-acceleration overhaul, pass 1 — same per-node override shape as
    /// ModelFamilyOverride above.</summary>
    public Dictionary<Guid, string> AspectModeOverride { get; set; } = [];
    public Dictionary<Guid, string> EffectiveAspectMode { get; set; } = [];
    /// <summary>Detection.DFineTensorRtMode per-node override — same shape as AspectModeOverride.</summary>
    public Dictionary<Guid, string> DFineTensorRtModeOverride { get; set; } = [];
    public Dictionary<Guid, string> EffectiveDFineTensorRtMode { get; set; } = [];
    /// <summary>Detection.Backend + the external inference service's URL/model/input-size — same
    /// per-node override shape as ModelFamilyOverride above (one Vision Service per node, so which
    /// backend it runs is inherently a per-node choice). The global default lives on
    /// Admin/Settings/Detection, including the "Test connection" model picker; this page only offers
    /// plain fields for a node that needs to point at a different service than the deployment
    /// default.</summary>
    public Dictionary<Guid, string> DetectionBackendOverride { get; set; } = [];
    public Dictionary<Guid, string> EffectiveDetectionBackend { get; set; } = [];
    public Dictionary<Guid, string> ExternalInferenceUrlOverride { get; set; } = [];
    public Dictionary<Guid, string> ExternalInferenceModelOverride { get; set; } = [];
    public Dictionary<Guid, int?> ExternalInferenceInputSizeOverride { get; set; } = [];
    /// <summary>Whether this node has its own external-inference API key override on file — never the
    /// key itself, same write-only convention as <see cref="Node.ClientCertPfxPassword"/>'s own form
    /// field: the value is never re-shown once saved, so the row can only report yes/no.</summary>
    public Dictionary<Guid, bool> HasExternalInferenceApiKeyOverride { get; set; } = [];
    public Dictionary<Guid, double?> DaysRemaining { get; set; } = [];

    /// <summary>Failover plan phase 1: the global LiveView.DirectStreaming value, shown as the
    /// "inherit" option label in each node's per-node override select.</summary>
    public string GlobalDirectStreaming { get; set; } = "Proxy";
    public bool GlobalAllowInsecureClientEndpoint { get; set; }
    /// <summary>Archive-storage: per-node archive volume free/total (off Node.Archive*Bytes) and the
    /// "days remaining" estimate for it, plus the per-node archive settings overrides. ArchiveRootPath
    /// is a Node column like StorageRootPath; Enabled/RetentionDays are SettingOverride(Scope.Node)
    /// rows like Retention.Days. "" on the enabled override select means inherit.</summary>
    public Dictionary<Guid, double?> ArchiveDaysRemaining { get; set; } = [];
    public Dictionary<Guid, string> ArchiveEnabledOverride { get; set; } = [];
    public Dictionary<Guid, bool> EffectiveArchiveEnabled { get; set; } = [];
    public Dictionary<Guid, int?> ArchiveRetentionOverride { get; set; } = [];
    public Dictionary<Guid, int> EffectiveArchiveRetentionDays { get; set; } = [];
    public Dictionary<Guid, int> StaleCameraCountByNode { get; set; } = [];
    public Dictionary<Guid, List<StaleCameraRow>> StaleCamerasByNode { get; set; } = [];
    /// <summary>Stale-save guard: a hash of every field this page's per-node form can submit, as it
    /// stood at the moment this GET rendered the row. Round-tripped through a hidden field and
    /// re-checked against a freshly-read snapshot at the top of OnPostUpdateAsync, before any write
    /// happens — see that check's own comment for why. Keyed by node id like every other per-node
    /// dictionary on this page.</summary>
    public Dictionary<Guid, string> Snapshot { get; set; } = [];
    public string? ErrorMessage { get; set; }

    /// <summary>One row of the warning-emoji tooltip's detail list — ClearsAtUtc is when this
    /// camera's *last* remaining stale segment on this node ages past retention (see
    /// StaleSegmentDetail's own doc comment for why it's the newest segment, not the oldest, that
    /// determines this), which is the same moment the camera drops out of the warning on its own.</summary>
    public record StaleCameraRow(string CameraName, DateTime ClearsAtUtc);

    /// <summary>A node's archive root must be short enough that a segment path built under it stays
    /// within Segment.FilePath's 450-char unique-indexed column (cam-{guid}/main/YYYY/MM/DD/HH/filename
    /// adds ~55 chars), and must be a distinct location from that node's own recording root so the
    /// node's orphan-import sweep never re-imports archived files. Returns null when valid.</summary>
    internal static string? ValidateArchiveRoot(string? archiveRoot, string? storageRoot)
    {
        if (string.IsNullOrWhiteSpace(archiveRoot)) return null;
        if (archiveRoot.Length > 300)
            return "Archive storage root is too long — keep it under 300 characters.";
        if (!string.IsNullOrWhiteSpace(storageRoot))
        {
            var a = archiveRoot.TrimEnd('/', '\\');
            var s = storageRoot.TrimEnd('/', '\\');
            if (a.Equals(s, StringComparison.OrdinalIgnoreCase)
                || a.StartsWith(s + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || a.StartsWith(s + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || s.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || s.StartsWith(a + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return "Archive storage root must be a separate location from this node's recording storage root.";
        }
        return null;
    }

    /// <summary>Normalizes an int-valued override the same way on both the render side and the
    /// verify side of the stale-save guard below, so e.g. "010" and "10" (or a null vs. an empty
    /// string) never register as a spurious mismatch — every int-typed override on this page is only
    /// ever written by this same handler as a plain <c>int.ToString()</c>, so the two sides always
    /// agree once both are pushed through the same parse.</summary>
    private static string NormalizeInt(string? raw) => int.TryParse(raw, out var v) ? v.ToString() : "";

    /// <summary>Stale-save guard (see <see cref="Snapshot"/>): hashes every field this page's per-node
    /// form can submit into one opaque token. Deliberately excludes anything this form can't edit
    /// (Status, LastSeenAt, StorageFreeBytes, ...) — those change on every heartbeat independently of
    /// any admin edit, and including them would make ordinary saves spuriously conflict with a node
    /// that simply phoned home in between. Also excludes the two write-only secret fields
    /// (ClientCertPfxPassword, Detection.ExternalInferenceApiKey's own value) since those already use
    /// "blank means leave alone" rather than "blank means clear" — they were never at risk of the
    /// silent-clobber bug this guard exists for, so they don't need to gate on it either. U+0001
    /// separates fields (rather than e.g. ',') since it can't appear in any of these values, and every
    /// null is folded to the distinct U+0000 sentinel so "null" and "" can never collide.</summary>
    private static string ComputeSnapshot(params string?[] parts)
    {
        var joined = string.Join('\u0001', parts.Select(p => p ?? "\u0000"));
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash);
    }

    private static string ComputeNodeSnapshot(Node n,
        string? retentionOverrideRaw, string? modelFamilyOverride, string? dfineWeightsOverride, string? yoloXSizeOverride,
        string? maxFpsOverrideRaw, string? aspectModeOverride, string? dfineTensorRtModeOverride,
        string? backendOverride, string? externalUrlOverride, string? externalModelOverride, string? externalInputSizeOverrideRaw,
        bool hasExternalApiKeyOverride, string? archiveEnabledOverride, string? archiveRetentionOverrideRaw,
        string? localModelNameOverride = null) =>
        ComputeSnapshot(
            n.Name, n.StorageRootPath, n.ArchiveRootPath, n.AiAccelerator?.ToString(), n.DisableAiObjectDetection.ToString(),
            n.DirectStreamingMode, n.AllowInsecureClientEndpoint?.ToString(), n.ClientEndpointHost, n.ClientCertPfxPath,
            n.PrimaryProxyId?.ToString(), n.BackupProxyId?.ToString(), n.BackupNodeId?.ToString(),
            NormalizeInt(retentionOverrideRaw), modelFamilyOverride ?? "", dfineWeightsOverride ?? "", yoloXSizeOverride ?? "",
            NormalizeInt(maxFpsOverrideRaw), aspectModeOverride ?? "", dfineTensorRtModeOverride ?? "",
            backendOverride ?? "", externalUrlOverride ?? "", externalModelOverride ?? "", NormalizeInt(externalInputSizeOverrideRaw),
            hasExternalApiKeyOverride.ToString(), archiveEnabledOverride ?? "", NormalizeInt(archiveRetentionOverrideRaw),
            localModelNameOverride ?? "");

    public async Task OnGetAsync()
    {
        Nodes = await nodeService.ListAsync();
        var cameras = await cameraService.ListAsync();
        CameraCountByNode = cameras
            .Where(c => c.NodeId is not null)
            .GroupBy(c => c.NodeId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        // Cameras/Index shows the same underlying data per-camera (which node(s) still hold its
        // stale footage); here it's inverted to "how many cameras have footage stranded on this
        // node" — a live query, no stored flag, same reasoning as the per-camera badge.
        var staleDetails = await cameraService.GetStaleSegmentDetailsAsync();
        StaleCameraCountByNode = staleDetails
            .GroupBy(d => d.NodeId)
            .ToDictionary(g => g.Key, g => g.Count());

        // Same resolution NodeService.GetConfigAsync uses to hand a retention value to this exact
        // node for this exact camera's orphaned footage (camera+node override -> node default ->
        // 30-day hardcoded fallback, matching StorageManager.SweepOrphanedCameraFolders' own
        // fallback constant) — so "falls off <date>" in the tooltip is the same date the node's own
        // sweep will actually act on, not a separate guess at it.
        StaleCamerasByNode = [];
        foreach (var d in staleDetails)
        {
            var retentionDays = await settings.GetAsync<int?>("Retention.Days", 30, cameraId: d.CameraId, nodeId: d.NodeId);
            var clearsAtUtc = d.NewestSegmentEndUtc.AddDays(retentionDays ?? 30);
            if (!StaleCamerasByNode.TryGetValue(d.NodeId, out var rows))
                StaleCamerasByNode[d.NodeId] = rows = [];
            rows.Add(new StaleCameraRow(d.CameraName, clearsAtUtc));
        }

        DaysRemaining = await nodeService.GetEstimatedDaysRemainingAsync();
        ArchiveDaysRemaining = await nodeService.GetArchiveEstimatedDaysRemainingAsync();
        GlobalDirectStreaming = await settings.GetAsync("LiveView.DirectStreaming", "Proxy");
        GlobalAllowInsecureClientEndpoint = await settings.GetAsync("LiveView.AllowInsecureClientEndpoint", false);
        AvailableProxies = (await proxyService.ListAsync()).Where(p => p.Enabled).ToList();

        foreach (var n in Nodes)
        {
            EffectiveRetentionDays[n.Id] = await settings.GetAsync("Retention.Days", 30, nodeId: n.Id);
            var ownOverride = await settings.GetOwnOverrideAsync(SettingScope.Node, n.Id, "Retention.Days");
            RetentionOverride[n.Id] = int.TryParse(ownOverride, out var days) ? days : null;

            ArchiveEnabledOverride[n.Id] = await settings.GetOwnOverrideAsync(SettingScope.Node, n.Id, "Archive.Enabled") ?? "";
            EffectiveArchiveEnabled[n.Id] = await settings.GetAsync("Archive.Enabled", false, nodeId: n.Id);
            var archiveRetentionOwn = await settings.GetOwnOverrideAsync(SettingScope.Node, n.Id, "Archive.RetentionDays");
            ArchiveRetentionOverride[n.Id] = int.TryParse(archiveRetentionOwn, out var ard) ? ard : null;
            EffectiveArchiveRetentionDays[n.Id] = await settings.GetAsync("Archive.RetentionDays", 0, nodeId: n.Id);

            ModelFamilyOverride[n.Id] = await settings.GetOwnOverrideAsync(SettingScope.Node, n.Id, "Detection.ModelFamily") ?? "";
            DFineWeightsOverride[n.Id] = await settings.GetOwnOverrideAsync(SettingScope.Node, n.Id, "Detection.DFineWeights") ?? "";
            YoloXSizeOverride[n.Id] = await settings.GetOwnOverrideAsync(SettingScope.Node, n.Id, "Detection.YoloXSize") ?? "";
            LocalModelNameOverride[n.Id] = await settings.GetOwnOverrideAsync(SettingScope.Node, n.Id, "Detection.LocalModelName") ?? "";
            var maxFpsOwn = await settings.GetOwnOverrideAsync(SettingScope.Node, n.Id, "Detection.MaxFps");
            MaxFpsOverride[n.Id] = int.TryParse(maxFpsOwn, out var mf) ? mf : null;
            EffectiveModelFamily[n.Id] = await settings.GetAsync("Detection.ModelFamily", "Auto", nodeId: n.Id);
            AspectModeOverride[n.Id] = await settings.GetOwnOverrideAsync(SettingScope.Node, n.Id, "Detection.AspectMode") ?? "";
            EffectiveAspectMode[n.Id] = await settings.GetAsync("Detection.AspectMode", "Letterbox", nodeId: n.Id);
            DFineTensorRtModeOverride[n.Id] = await settings.GetOwnOverrideAsync(SettingScope.Node, n.Id, "Detection.DFineTensorRtMode") ?? "";
            EffectiveDFineTensorRtMode[n.Id] = await settings.GetAsync("Detection.DFineTensorRtMode", "Off", nodeId: n.Id);

            DetectionBackendOverride[n.Id] = await settings.GetOwnOverrideAsync(SettingScope.Node, n.Id, "Detection.Backend") ?? "";
            EffectiveDetectionBackend[n.Id] = await settings.GetAsync("Detection.Backend", "BuiltIn", nodeId: n.Id);
            ExternalInferenceUrlOverride[n.Id] = await settings.GetOwnOverrideAsync(SettingScope.Node, n.Id, "Detection.ExternalInferenceUrl") ?? "";
            ExternalInferenceModelOverride[n.Id] = await settings.GetOwnOverrideAsync(SettingScope.Node, n.Id, "Detection.ExternalInferenceModel") ?? "";
            var externalInputSizeOwn = await settings.GetOwnOverrideAsync(SettingScope.Node, n.Id, "Detection.ExternalInferenceInputSize");
            ExternalInferenceInputSizeOverride[n.Id] = int.TryParse(externalInputSizeOwn, out var eis) ? eis : null;
            HasExternalInferenceApiKeyOverride[n.Id] = !string.IsNullOrEmpty(
                await settings.GetOwnOverrideAsync(SettingScope.Node, n.Id, "Detection.ExternalInferenceApiKey"));

            // Stale-save guard — see Snapshot's own doc comment. Computed from the exact raw values
            // just read above (not the parsed/defaulted dictionary values) so it matches byte-for-byte
            // what OnPostUpdateAsync recomputes from a fresh read at save time.
            Snapshot[n.Id] = ComputeNodeSnapshot(n, ownOverride, ModelFamilyOverride[n.Id], DFineWeightsOverride[n.Id],
                YoloXSizeOverride[n.Id], maxFpsOwn, AspectModeOverride[n.Id], DFineTensorRtModeOverride[n.Id],
                DetectionBackendOverride[n.Id], ExternalInferenceUrlOverride[n.Id], ExternalInferenceModelOverride[n.Id],
                externalInputSizeOwn, HasExternalInferenceApiKeyOverride[n.Id], ArchiveEnabledOverride[n.Id], archiveRetentionOwn,
                LocalModelNameOverride[n.Id]);
        }
    }

    /// <summary>The "Refresh models" button's fetch target — proxies this node's own
    /// <c>GET /vision/models</c> via <see cref="LarisVMS.Web.Services.NodeControlClient"/>, so the
    /// Detection.LocalModelName field's datalist and warning line reflect what
    /// <c>LarisVMS.Vision.Models.ModelDiscovery</c> actually found on the node just now, not whatever
    /// was true the last time this page was loaded.</summary>
    public async Task<IActionResult> OnGetLocalModelsAsync(Guid id, CancellationToken ct)
    {
        var node = (await nodeService.ListAsync()).FirstOrDefault(n => n.Id == id);
        if (node is null) return new JsonResult(Array.Empty<object>());

        var models = await nodeControlClient.GetVisionModelsAsync(node, ct);
        return new JsonResult(models);
    }

    public async Task<IActionResult> OnPostUpdateAsync(Guid id, string name, string? storageRootPath, int? retentionDaysOverride,
        string? aiAccelerator, string? modelFamilyOverride, string? dfineWeightsOverride, string? yoloXSizeOverride,
        int? maxFpsOverride, string? aspectModeOverride, string? dfineTensorRtModeOverride,
        string? backendOverride, string? externalUrlOverride, string? externalModelOverride, int? externalInputSizeOverride,
        string? externalApiKeyOverride,
        string? archiveRootPath, string? archiveEnabledOverride, int? archiveRetentionDaysOverride,
        string? directStreamingMode, string? allowInsecureClientEndpoint, string? clientEndpointHost,
        string? clientCertPfxPath, string? clientCertPfxPassword,
        string? primaryProxyId, string? backupProxyId,
        string? backupNodeId, bool disableAiObjectDetection = false, string? snapshot = null,
        string? localModelNameOverride = null)
    {
        try
        {
            // Storage path is per-node and required — a node with none records nothing (GetConfigAsync
            // serves it no cameras). A node that already has one can't have it cleared here.
            if (string.IsNullOrWhiteSpace(storageRootPath))
            {
                ErrorMessage = "A storage path is required — this is where the node records to.";
                await OnGetAsync();
                return Page();
            }
            var archiveError = ValidateArchiveRoot(archiveRootPath, storageRootPath);
            if (archiveError is not null)
            {
                ErrorMessage = archiveError;
                await OnGetAsync();
                return Page();
            }

            // Blank means "inherit the global service URL" — same convention every other override on
            // this page uses. A non-blank value must be a real http(s) URL; same check
            // Admin/Settings/Detection's own OnPostAsync applies to the global setting.
            externalUrlOverride = string.IsNullOrWhiteSpace(externalUrlOverride) ? null : externalUrlOverride.Trim().TrimEnd('/');
            if (externalUrlOverride is not null
                && (!Uri.TryCreate(externalUrlOverride, UriKind.Absolute, out var externalUri) || externalUri.Scheme is not ("http" or "https")))
            {
                ErrorMessage = "The external inference service override must be a full URL like http://192.168.1.50:8080, or blank to inherit.";
                await OnGetAsync();
                return Page();
            }
            if (externalInputSizeOverride is { } size && (size <= 0 || size % 32 != 0))
            {
                ErrorMessage = "The external inference input-size override must be a positive multiple of 32 (e.g. 640), or blank to inherit.";
                await OnGetAsync();
                return Page();
            }

            // A node whose EFFECTIVE backend (this override, or the inherited global default) is
            // "ExternalHttp" needs an effective URL and model from *somewhere* — either this node's
            // own overrides or the global Detection settings — or CameraPipelineManager throws
            // building HttpDetectionEngine ("baseUrl" empty) for every camera on the node and none of
            // them get watched. Admin/Settings/Detection's own OnPostAsync already guards this
            // combination for the global values; this is the node-override analogue, since setting
            // just the backend override here (leaving URL/model blank to "inherit") silently produces
            // exactly that broken combination if the global URL/model were never actually saved.
            var effectiveBackend = string.IsNullOrEmpty(backendOverride)
                ? await settings.GetAsync("Detection.Backend", "BuiltIn")
                : backendOverride;
            if (string.Equals(effectiveBackend, "ExternalHttp", StringComparison.OrdinalIgnoreCase))
            {
                var effectiveUrl = externalUrlOverride ?? await settings.GetAsync("Detection.ExternalInferenceUrl", "");
                var effectiveModel = string.IsNullOrWhiteSpace(externalModelOverride)
                    ? await settings.GetAsync("Detection.ExternalInferenceModel", "")
                    : externalModelOverride;
                if (string.IsNullOrEmpty(effectiveUrl) || string.IsNullOrEmpty(effectiveModel))
                {
                    ErrorMessage = "This node resolves to the external HTTP backend but has no URL and model to use — " +
                        "either fill in the URL/model overrides here, or save them on Admin/Settings/Detection first.";
                    await OnGetAsync();
                    return Page();
                }
            }

            // Pre-edit state first, so the entry reports what actually changed — including the
            // per-node retention override, which previously rode along inside this same generic
            // "Node.Update" entry with nothing to indicate it had been touched at all. No secret
            // fields here: MediaSigningKey is generated internally and never edited through this form.
            var before = (await nodeService.ListAsync()).FirstOrDefault(n => n.Id == id);
            var oldRetentionOverride = await settings.GetOwnOverrideAsync(SettingScope.Node, id, "Retention.Days");
            var oldModelFamilyOverride = await settings.GetOwnOverrideAsync(SettingScope.Node, id, "Detection.ModelFamily");
            var oldDFineWeightsOverride = await settings.GetOwnOverrideAsync(SettingScope.Node, id, "Detection.DFineWeights");
            var oldYoloXSizeOverride = await settings.GetOwnOverrideAsync(SettingScope.Node, id, "Detection.YoloXSize");
            var oldMaxFpsOverride = await settings.GetOwnOverrideAsync(SettingScope.Node, id, "Detection.MaxFps");
            var oldAspectModeOverride = await settings.GetOwnOverrideAsync(SettingScope.Node, id, "Detection.AspectMode");
            var oldDFineTensorRtModeOverride = await settings.GetOwnOverrideAsync(SettingScope.Node, id, "Detection.DFineTensorRtMode");
            var oldBackendOverride = await settings.GetOwnOverrideAsync(SettingScope.Node, id, "Detection.Backend");
            var oldExternalUrlOverride = await settings.GetOwnOverrideAsync(SettingScope.Node, id, "Detection.ExternalInferenceUrl");
            var oldExternalModelOverride = await settings.GetOwnOverrideAsync(SettingScope.Node, id, "Detection.ExternalInferenceModel");
            var oldExternalInputSizeOverride = await settings.GetOwnOverrideAsync(SettingScope.Node, id, "Detection.ExternalInferenceInputSize");
            var oldArchiveEnabledOverride = await settings.GetOwnOverrideAsync(SettingScope.Node, id, "Archive.Enabled");
            var oldArchiveRetentionOverride = await settings.GetOwnOverrideAsync(SettingScope.Node, id, "Archive.RetentionDays");
            var oldLocalModelNameOverride = await settings.GetOwnOverrideAsync(SettingScope.Node, id, "Detection.LocalModelName");
            var oldHasExternalApiKeyOverride = !string.IsNullOrEmpty(
                await settings.GetOwnOverrideAsync(SettingScope.Node, id, "Detection.ExternalInferenceApiKey"));

            if (before is null)
            {
                ErrorMessage = "That node no longer exists.";
                await OnGetAsync();
                return Page();
            }

            // Stale-save guard — see Snapshot's own doc comment on the GET side. Recomputed here from
            // a fresh read of every field this form can submit, *before* any of them get written, and
            // compared against the hash the form was rendered with. A mismatch means something else
            // (another tab, another admin, a reopened/back-navigated page) changed this node's config
            // since this page was loaded — reject the save outright rather than writing this stale
            // snapshot's blanks over whatever changed in the meantime. This is what actually caught
            // the bug where a stale Nodes page silently cleared a working Detection.Backend/external-
            // service override back to "inherit" on save.
            var currentSnapshot = ComputeNodeSnapshot(before, oldRetentionOverride, oldModelFamilyOverride,
                oldDFineWeightsOverride, oldYoloXSizeOverride, oldMaxFpsOverride, oldAspectModeOverride,
                oldDFineTensorRtModeOverride, oldBackendOverride, oldExternalUrlOverride, oldExternalModelOverride,
                oldExternalInputSizeOverride, oldHasExternalApiKeyOverride, oldArchiveEnabledOverride, oldArchiveRetentionOverride,
                oldLocalModelNameOverride);
            if (!string.Equals(snapshot, currentSnapshot, StringComparison.Ordinal))
            {
                ErrorMessage = $"{before.Name}'s settings changed (in another tab, or by someone else) since this page " +
                    "was loaded — nothing was saved, to avoid overwriting that change. Reload the page and re-apply your edit.";
                await OnGetAsync();
                return Page();
            }

            // "" (blank/Auto) resolves as null — see NodeConfigResponse.AiAccelerator's own doc
            // comment for why Auto (not an explicit choice) is the safe default.
            var accelerator = Enum.TryParse<AiAccelerator>(aiAccelerator, out var acc) ? acc : (AiAccelerator?)null;

            // Failover plan phase 1: tri-state select ("" inherit / "true" / "false"); pfx password
            // is write-only — a blank field means "leave as-is".
            bool? insecureOverride = allowInsecureClientEndpoint switch
            {
                "true" => true,
                "false" => false,
                _ => null,
            };
            Guid? primaryProxy = Guid.TryParse(primaryProxyId, out var pp) ? pp : null;
            Guid? backupProxy = Guid.TryParse(backupProxyId, out var bp) ? bp : null;
            Guid? backupNode = Guid.TryParse(backupNodeId, out var bn) ? bn : null;
            var clientEndpoint = new NodeClientEndpointUpdate(
                string.IsNullOrWhiteSpace(directStreamingMode) ? null : directStreamingMode,
                insecureOverride, clientEndpointHost, clientCertPfxPath,
                string.IsNullOrEmpty(clientCertPfxPassword) ? null : clientCertPfxPassword,
                primaryProxy, backupProxy, backupNode, disableAiObjectDetection);

            await nodeService.UpdateAsync(id, name, storageRootPath, accelerator, archiveRootPath, clientEndpoint);
            await settings.SetOverrideAsync(SettingScope.Node, id, "Retention.Days",
                retentionDaysOverride?.ToString(), User.Identity?.Name);
            await settings.SetOverrideAsync(SettingScope.Node, id, "Detection.ModelFamily",
                string.IsNullOrEmpty(modelFamilyOverride) ? null : modelFamilyOverride, User.Identity?.Name);
            await settings.SetOverrideAsync(SettingScope.Node, id, "Detection.DFineWeights",
                string.IsNullOrEmpty(dfineWeightsOverride) ? null : dfineWeightsOverride, User.Identity?.Name);
            await settings.SetOverrideAsync(SettingScope.Node, id, "Detection.YoloXSize",
                string.IsNullOrEmpty(yoloXSizeOverride) ? null : yoloXSizeOverride, User.Identity?.Name);
            await settings.SetOverrideAsync(SettingScope.Node, id, "Detection.LocalModelName",
                string.IsNullOrEmpty(localModelNameOverride) ? null : localModelNameOverride, User.Identity?.Name);
            await settings.SetOverrideAsync(SettingScope.Node, id, "Detection.MaxFps",
                maxFpsOverride?.ToString(), User.Identity?.Name);
            await settings.SetOverrideAsync(SettingScope.Node, id, "Detection.AspectMode",
                string.IsNullOrEmpty(aspectModeOverride) ? null : aspectModeOverride, User.Identity?.Name);
            await settings.SetOverrideAsync(SettingScope.Node, id, "Detection.DFineTensorRtMode",
                string.IsNullOrEmpty(dfineTensorRtModeOverride) ? null : dfineTensorRtModeOverride, User.Identity?.Name);
            await settings.SetOverrideAsync(SettingScope.Node, id, "Detection.Backend",
                string.IsNullOrEmpty(backendOverride) ? null : backendOverride, User.Identity?.Name);
            await settings.SetOverrideAsync(SettingScope.Node, id, "Detection.ExternalInferenceUrl",
                externalUrlOverride, User.Identity?.Name);
            await settings.SetOverrideAsync(SettingScope.Node, id, "Detection.ExternalInferenceModel",
                string.IsNullOrEmpty(externalModelOverride) ? null : externalModelOverride, User.Identity?.Name);
            await settings.SetOverrideAsync(SettingScope.Node, id, "Detection.ExternalInferenceInputSize",
                externalInputSizeOverride?.ToString(), User.Identity?.Name);
            // Write-only, same convention as clientCertPfxPassword above: blank means "leave the
            // existing override (if any) alone", not "clear it" — the field never re-shows a saved
            // key, so there is no way to distinguish "never touched" from "clear it" otherwise.
            var externalApiKeyOverrideChanged = !string.IsNullOrWhiteSpace(externalApiKeyOverride);
            if (externalApiKeyOverrideChanged)
                await settings.SetOverrideAsync(SettingScope.Node, id, "Detection.ExternalInferenceApiKey",
                    externalApiKeyOverride!.Trim(), User.Identity?.Name);
            await settings.SetOverrideAsync(SettingScope.Node, id, "Archive.Enabled",
                string.IsNullOrEmpty(archiveEnabledOverride) ? null : archiveEnabledOverride, User.Identity?.Name);
            await settings.SetOverrideAsync(SettingScope.Node, id, "Archive.RetentionDays",
                archiveRetentionDaysOverride?.ToString(), User.Identity?.Name);

            var details = AuditDiff.Build(
                AuditDiff.Of("Name", before?.Name, name),
                AuditDiff.Of("Storage root", before?.StorageRootPath, storageRootPath),
                AuditDiff.Of("Retention override", oldRetentionOverride, retentionDaysOverride?.ToString()),
                AuditDiff.Of("AI accelerator", before?.AiAccelerator?.ToString(), accelerator?.ToString()),
                AuditDiff.Of("Detection model override", oldModelFamilyOverride, modelFamilyOverride),
                AuditDiff.Of("D-FINE weights override", oldDFineWeightsOverride, dfineWeightsOverride),
                AuditDiff.Of("YOLOX size override", oldYoloXSizeOverride, yoloXSizeOverride),
                AuditDiff.Of("Local model name override", oldLocalModelNameOverride, localModelNameOverride),
                AuditDiff.Of("Max detection fps override", oldMaxFpsOverride, maxFpsOverride?.ToString()),
                AuditDiff.Of("Aspect fitting override", oldAspectModeOverride, aspectModeOverride),
                AuditDiff.Of("D-FINE TensorRT override", oldDFineTensorRtModeOverride, dfineTensorRtModeOverride),
                AuditDiff.Of("Detection backend override", oldBackendOverride, backendOverride),
                AuditDiff.Of("External inference URL override", oldExternalUrlOverride, externalUrlOverride),
                AuditDiff.Of("External inference model override", oldExternalModelOverride, externalModelOverride),
                AuditDiff.Of("External inference input-size override", oldExternalInputSizeOverride, externalInputSizeOverride?.ToString()),
                AuditDiff.SecretChanged("External inference API key override", externalApiKeyOverrideChanged),
                AuditDiff.Of("Archive root", before?.ArchiveRootPath, archiveRootPath),
                AuditDiff.Of("Archive enabled override", oldArchiveEnabledOverride, archiveEnabledOverride),
                AuditDiff.Of("Archive retention override", oldArchiveRetentionOverride, archiveRetentionDaysOverride?.ToString()),
                AuditDiff.Of("Direct streaming mode", before?.DirectStreamingMode, clientEndpoint.DirectStreamingMode),
                AuditDiff.Of("Allow insecure client endpoint", before?.AllowInsecureClientEndpoint?.ToString(), insecureOverride?.ToString()),
                AuditDiff.Of("Client endpoint host", before?.ClientEndpointHost, clientEndpointHost),
                AuditDiff.Of("Client cert pfx path", before?.ClientCertPfxPath, clientCertPfxPath),
                AuditDiff.Of("Client cert pfx password", null, clientEndpoint.ClientCertPfxPassword is null ? null : "(changed)"),
                AuditDiff.Of("Primary proxy", before?.PrimaryProxyId?.ToString(), primaryProxy?.ToString()),
                AuditDiff.Of("Backup proxy", before?.BackupProxyId?.ToString(), backupProxy?.ToString()),
                AuditDiff.Of("Backup node", before?.BackupNodeId?.ToString(), backupNode?.ToString()),
                AuditDiff.Of("Recording only (AI off)", before?.DisableAiObjectDetection.ToString(), disableAiObjectDetection.ToString()));

            await LogAsync("Node.Update", details is null ? $"{name} ({id})" : $"{name} ({id}) — {details}");

            // Fire-and-forget, deliberately not awaited: a saved override should take effect promptly
            // rather than waiting out this node's own 30s reconcile poll, but a slow/unreachable node
            // must never hold up this page's own redirect — that 30s poll remains the correctness
            // backstop if this call doesn't land. CancellationToken.None (not HttpContext.RequestAborted),
            // since the call should keep running after this response is sent, not be cancelled by it.
            _ = nodeControlClient.TriggerReconcileAsync(before);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            await OnGetAsync();
            return Page();
        }

        return RedirectToPage();
    }

    /// <summary>Restarts a node's own Windows Service — not the machine. Proxied through this tier the
    /// same way every other Web→Node call is (the browser never talks to a node directly), authorized
    /// by a short-lived token signed with that node's own MediaSigningKey.
    ///
    /// Recording on this node's cameras stops for the few seconds the service is down, which is why
    /// this is behind Nodes.Edit and audited like any other node change.</summary>
    public async Task<IActionResult> OnPostRestartAsync(Guid id)
    {
        var node = (await nodeService.ListAsync()).FirstOrDefault(n => n.Id == id);
        if (node is null)
        {
            StatusMessage = "That node no longer exists.";
            StatusIsError = true;
            return RedirectToPage();
        }

        // Same readiness requirement as the live/playback proxies: without a reported IP, port and
        // signing key there is no authenticated way to reach this node at all.
        if (node is not { LastIpAddress: { } ip, LivePort: { } port, MediaSigningKey: { } key })
        {
            StatusMessage = $"{node.Name} hasn't reported its address yet — it needs at least one heartbeat before it can be restarted from here.";
            StatusIsError = true;
            return RedirectToPage();
        }

        var token = MediaToken.IssueForNodeControl("restart", key, TimeSpan.FromSeconds(30));
        var client = httpFactory.CreateClient();
        // Short: the node answers before it actually stops (see its own /restart comment), so a slow
        // response here means it's unreachable, not that it's busy restarting.
        client.Timeout = TimeSpan.FromSeconds(15);

        try
        {
            // See MediaTokenRequest's own doc comment: also sent as ?token= below for a node that
            // hasn't updated yet.
            var response = await client.SendAsync(MediaTokenRequest.Create(HttpMethod.Post,
                $"http://{ip}:{port}/restart?token={Uri.EscapeDataString(token)}", token));

            if (response.IsSuccessStatusCode)
            {
                StatusMessage = $"{node.Name} is restarting. It should reappear as online within a minute.";
                StatusIsError = false;
                await LogAsync("Node.Restart", $"{node.Name} ({id})");
            }
            else
            {
                // The node's own message is more specific than anything this tier could infer (e.g. a
                // missing updater binary), so it's surfaced verbatim rather than replaced.
                var body = (await response.Content.ReadAsStringAsync()).Trim();
                StatusMessage = $"{node.Name} refused the restart ({(int)response.StatusCode})" +
                    (string.IsNullOrEmpty(body) ? "." : $": {body}");
                StatusIsError = true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = $"Could not reach {node.Name}: {ex.Message}";
            StatusIsError = true;
        }

        return RedirectToPage();
    }

    /// <summary>Failover plan phase 5a: recovery for a node locked out because its rolling check-in
    /// nonce got out of sync (a heartbeat response the node never received — it keeps resending the
    /// stale nonce, which the server now rejects 401). Clears the stored nonce so the node's next
    /// check-in is accepted as a not-yet-nonced one and re-syncs. A no-op risk on a healthy node.</summary>
    public async Task<IActionResult> OnPostResetAuthAsync(Guid id)
    {
        var node = (await nodeService.ListAsync()).FirstOrDefault(n => n.Id == id);
        if (node is null)
        {
            StatusMessage = "That node no longer exists.";
            StatusIsError = true;
            return RedirectToPage();
        }

        await nodeService.ResetAuthAsync(id, rotateSecretNow: false);
        await LogAsync("Node.ResetAuth", $"{node.Name} ({id}) — check-in nonce cleared");
        StatusMessage = $"{node.Name}: check-in nonce cleared. If it was locked out, it should re-sync within a heartbeat cycle.";
        StatusIsError = false;
        return RedirectToPage();
    }

    /// <summary>Failover plan phase 3: toggle a node into/out of maintenance mode. On = its cameras
    /// fail over to its backup node on RecordingFailoverService's next tick (~15s) and stay there
    /// until turned off, regardless of the node's own health. Confirm dialog is client-side.</summary>
    public async Task<IActionResult> OnPostSetMaintenanceAsync(Guid id, bool enabled)
    {
        var node = (await nodeService.ListAsync()).FirstOrDefault(n => n.Id == id);
        if (node is null)
        {
            StatusMessage = "That node no longer exists.";
            StatusIsError = true;
            return RedirectToPage();
        }

        await nodeService.SetMaintenanceAsync(id, enabled, User.Identity?.Name);
        await LogAsync(enabled ? "Node.MaintenanceEntered" : "Node.MaintenanceExited", $"{node.Name} ({id})");
        StatusMessage = enabled
            ? $"{node.Name} is in maintenance — its cameras move to its backup node within ~15 seconds and stay there until you turn maintenance off."
            : $"{node.Name} left maintenance — its cameras return to it once it is a confirmed-healthy quorum (or immediately if it stayed healthy).";
        StatusIsError = false;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        var node = (await nodeService.ListAsync()).FirstOrDefault(n => n.Id == id);
        await nodeService.DeleteAsync(id);
        await LogAsync("Node.Delete", $"{node?.Name ?? "?"} ({id})");
        return RedirectToPage();
    }

    private Task LogAsync(string action, string details) =>
        auditService.LogAsync(action, User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,
            User.Identity?.Name, HttpContext.Connection.RemoteIpAddress?.ToString(), details);
}
