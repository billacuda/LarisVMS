using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Interfaces;
using LarisVMS.Web.Services;

namespace LarisVMS.Web.Pages.Admin.Settings;

/// <summary>Global AI-detection defaults — the base of the Camera &rarr; Node &rarr; Global chain a
/// per-camera override on Cameras/Edit falls back to for Confidence/IoU/StreamRole. Split into its
/// own tab the same way Recording's own settings were, since none of these previously had any admin
/// UI at all (the Setting rows existed and were already read by NodeService, just never written from
/// anywhere).</summary>
[Authorize("Settings.Edit")]
public class DetectionModel(ISettingsResolver settings, IAuditService auditService, ExternalInferenceProbe externalProbe) : PageModel
{
    [BindProperty] public double Confidence { get; set; } = 0.35;
    [BindProperty] public double Iou { get; set; } = 0.5;
    [BindProperty] public string StreamRole { get; set; } = "Sub";
    /// <summary>Deployment default for how a camera's reported detection-stream dimensions are
    /// oriented — "Auto", "Landscape" or "Portrait". Almost always overridden per camera rather than
    /// set here, since it describes one device's mounting; see LarisVMS.Node.DetectionOrientation.</summary>
    [BindProperty] public string Orientation { get; set; } = "Auto";
    [BindProperty] public bool ReportIdleDetections { get; set; }
    [BindProperty] public int IdleTimeoutSeconds { get; set; } = 10;
    /// <summary>When on (the default), the Vision Service finalizes a detection's snapshot promptly
    /// once its object leaves frame — no instance classified moving for <see cref="DepartureGraceSeconds"/>
    /// — instead of holding the span open long enough for a later, unrelated object of the same type
    /// to be merged into it. Off restores the previous behaviour. Global; changing it restarts each
    /// camera's detection pipeline. (The raw-detection-box jitter rejection this once also governed
    /// is now the per-camera <see cref="RejectMotionJitter"/>.)</summary>
    [BindProperty] public bool SnapshotMotionAccuracy { get; set; } = true;
    /// <summary>Global default for the per-camera box-jitter rejection (key "Detection.RejectMotionJitter").
    /// Off by default: it smooths the movement check so a distant parked vehicle whose box wobbles
    /// doesn't count as moving, but on a noisier model it has also suppressed genuinely slow movers,
    /// so it is opt-in — usually set per camera on Cameras/Edit. Restarts each pipeline.</summary>
    [BindProperty] public bool RejectMotionJitter { get; set; }
    /// <summary>Global default (per-camera on Cameras/Edit) for the jitter-rejection pixel floor
    /// (key "Detection.MotionJitterPixels", 1-15): minimum real centroid travel before a track
    /// counts as moving. Only acted on where <see cref="RejectMotionJitter"/> is on. Higher rejects
    /// more wobble at the cost of ignoring slower real movement.</summary>
    [BindProperty] public int MotionJitterPixels { get; set; } = 3;
    /// <summary>How long (seconds, 1-10) a span with no moving instance is held before
    /// <see cref="SnapshotMotionAccuracy"/>'s early-finalize flushes it as "the object left frame".
    /// Global; was a fixed 5s. Key "Detection.DepartureGraceSeconds".</summary>
    [BindProperty] public int DepartureGraceSeconds { get; set; } = 5;
    /// <summary>Global, 0-100. How much extra room the eager sub-frame snapshot crop keeps around a
    /// reported box, as a percentage of the box's own size, so a viewer can see where the object is
    /// relative to what's around it rather than a razor-tight crop on just the box. Deliberately the
    /// same value regardless of Backend (built-in or ExternalHttp) — see
    /// NodeConfigResponse.SnapshotMarginPercent's own doc comment for the inconsistency this setting
    /// replaces (the two pixel formats used to disagree, and Slice mode with the external backend
    /// used no margin at all). Does not affect the separate, larger-by-design segment-seek crop
    /// margin (30%), which also compensates for Sub/Main stream timing drift.</summary>
    [BindProperty] public int SnapshotMarginPercent { get; set; } = 12;
    /// <summary>Node-scoped ceiling on frames/sec per camera reaching the model. The Vision Service
    /// still decodes the Sub stream in real time; an ffmpeg fps= filter drops the surplus before
    /// inference so the GPU idles between frames. 0 = no cap. 10 is plenty for object tracking.</summary>
    [BindProperty] public int MaxFps { get; set; } = 10;
    /// <summary>Node-scoped, not this page's other global-default fields' Camera-override sibling —
    /// see NodeConfigResponse.DetectionModelFamily's own doc comment for why. Auto → YOLOX; DFine is
    /// selectable but experimental (labelled so in the UI); RF-DETR still renders disabled (no decoder).</summary>
    [BindProperty] public string ModelFamily { get; set; } = "Auto";
    [BindProperty] public string DFineWeights { get; set; } = "Obj2Coco";
    /// <summary>Node-scoped like ModelFamily — how D-FINE uses TensorRT: "Off" (default), "FP32", or
    /// "FP16". Only acted on for a D-FINE pipeline on a node that also has the machine-local
    /// Vision:EnableTensorRt set. FP16 needs the bundled *.fp16.onnx mixed-precision model files.</summary>
    [BindProperty] public string DFineTensorRtMode { get; set; } = "Off";
    /// <summary>Node-scoped like ModelFamily — the YOLOX model size (Nano/Tiny/S/M/L/X). Only
    /// meaningful when the family resolves to YOLOX; the node fetches the chosen ONNX from the server
    /// on first use (YOLOX models aren't bundled).</summary>
    [BindProperty] public string YoloXSize { get; set; } = "S";
    /// <summary>Detection/hardware-acceleration overhaul, pass 1 — node-scoped like ModelFamily
    /// above (Admin/Nodes carries the per-node override), not this page's other global-default
    /// fields' Camera-override sibling. Only "Letterbox"/"Stretch" are implemented; AspectMatched
    /// is reserved for a future pass and never offered here.</summary>
    [BindProperty] public string AspectMode { get; set; } = "Letterbox";
    /// <summary>Pass 4a — node-scoped, opt-in. Moves per-frame colour conversion + normalize off the
    /// CPU onto the accelerator (an ONNX preprocessing head that runs on CUDA/DirectML/OpenVINO
    /// alike). Restarts each detection pipeline when toggled.</summary>
    [BindProperty] public bool GpuPreprocessing { get; set; }

    // ── External HTTP inference backend (key prefix "Detection.External*", node-scoped) ──────────
    /// <summary>"BuiltIn" runs a bundled model on the local accelerator; "ExternalHttp" runs no local
    /// model and POSTs each 640-scaled frame to <see cref="ExternalUrl"/> instead. Node-scoped.</summary>
    [BindProperty] public string Backend { get; set; } = "BuiltIn";
    /// <summary>The external service's base URL including its port, e.g. <c>http://192.168.1.50:8080</c>.
    /// Validated as an absolute http(s) URL. Only meaningful when <see cref="Backend"/> is "ExternalHttp".</summary>
    [BindProperty] public string? ExternalUrl { get; set; }
    /// <summary>The model to request (<c>?model=</c>) — chosen from the picklist the "Test connection"
    /// button discovers via the service's own <c>GET /v1/models</c>.</summary>
    [BindProperty] public string? ExternalModel { get; set; }
    /// <summary>The chosen model's square input size, carried in a hidden field the model picklist's
    /// JS keeps in sync (each option knows its own size). Re-clamped to a positive multiple of 32 on
    /// save; NodeService re-clamps again since it is stored as a free-form string.</summary>
    [BindProperty] public int ExternalInputSize { get; set; } = 640;
    /// <summary>Bearer token for the external service. Never re-populated into the form on load —
    /// same "blank means unchanged" convention as Email's SmtpPassword/GraphClientSecret — so a blank
    /// submission only happens when the operator genuinely left it alone; <see cref="HasStoredExternalApiKey"/>
    /// tells the page whether one is already on file.</summary>
    [BindProperty] public string? ExternalApiKey { get; set; }
    public bool HasStoredExternalApiKey { get; private set; }
    /// <summary>"Auto" (default, today's JPEG behaviour) / "Jpeg" / "PixelsYuv420" / "PixelsBgra" —
    /// see <c>ExternalInferenceTransport</c>'s own doc comment on the Vision side. Picking a raw-pixel
    /// mode only makes sense once "Test connection" confirms the service advertises the matching
    /// <c>pixels_*</c> token in its <c>input_modes</c> (surfaced per model in <see cref="AvailableModels"/>).</summary>
    [BindProperty] public string ExternalTransport { get; set; } = "Auto";

    /// <summary>Populated only by <see cref="OnPostTestConnectionAsync"/> — the discovered model list
    /// the picklist renders, plus (best effort) the service's /healthz readout.</summary>
    public IReadOnlyList<ExternalModelInfo> AvailableModels { get; private set; } = [];
    public ExternalHealthResponse? ProbeHealth { get; private set; }

    public string? SavedMessage { get; set; }
    public bool StatusIsError { get; set; }

    private string? CurrentUserId => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    public async Task OnGetAsync()
    {
        Confidence = await settings.GetAsync("Detection.Confidence", 0.35);
        Iou = await settings.GetAsync("Detection.Iou", 0.5);
        StreamRole = await settings.GetAsync("AiDetection.StreamRole", "Sub");
        Orientation = await settings.GetAsync("AiDetection.Orientation", "Auto");
        ReportIdleDetections = await settings.GetAsync("Detection.ReportIdleDetections", false);
        IdleTimeoutSeconds = await settings.GetAsync("Detection.IdleTimeoutSeconds", 10);
        SnapshotMotionAccuracy = await settings.GetAsync("Detection.SnapshotMotionAccuracy", true);
        RejectMotionJitter = await settings.GetAsync("Detection.RejectMotionJitter", false);
        MotionJitterPixels = await settings.GetAsync("Detection.MotionJitterPixels", 3);
        DepartureGraceSeconds = await settings.GetAsync("Detection.DepartureGraceSeconds", 5);
        SnapshotMarginPercent = await settings.GetAsync("Detection.SnapshotMarginPercent", 12);
        MaxFps = await settings.GetAsync("Detection.MaxFps", 10);
        ModelFamily = await settings.GetAsync("Detection.ModelFamily", "Auto");
        DFineWeights = await settings.GetAsync("Detection.DFineWeights", "Obj2Coco");
        DFineTensorRtMode = await settings.GetAsync("Detection.DFineTensorRtMode", "Off");
        YoloXSize = await settings.GetAsync("Detection.YoloXSize", "S");
        AspectMode = await settings.GetAsync("Detection.AspectMode", "Letterbox");
        GpuPreprocessing = await settings.GetAsync("Detection.GpuPreprocessing", false);
        Backend = await settings.GetAsync("Detection.Backend", "BuiltIn");
        ExternalUrl = await settings.GetAsync("Detection.ExternalInferenceUrl", "");
        ExternalModel = await settings.GetAsync("Detection.ExternalInferenceModel", "");
        ExternalInputSize = await settings.GetAsync("Detection.ExternalInferenceInputSize", 640);
        HasStoredExternalApiKey = !string.IsNullOrEmpty(await settings.GetAsync("Detection.ExternalInferenceApiKey", ""));
        ExternalTransport = await settings.GetAsync("Detection.ExternalInferenceTransport", "Auto");
    }

    /// <summary>Named rather than the unnamed OnPostAsync (the convention Email's own Save follows)
    /// because this page has a second handler. The form carries no explicit action, so the browser
    /// posts it to the current document URL — and after "Test connection" that URL still reads
    /// <c>?handler=TestConnection</c>. An unnamed Save button inherited that handler, so clicking Save
    /// right after a successful test silently re-ran the probe: the operator saw a green "Connected…"
    /// alert, nothing was written, and the next page load was back on the built-in backend.</summary>
    public async Task<IActionResult> OnPostSaveAsync()
    {
        var by = User.Identity?.Name;

        var oldConfidence = await settings.GetAsync("Detection.Confidence", 0.35);
        var oldIou = await settings.GetAsync("Detection.Iou", 0.5);
        var oldStreamRole = await settings.GetAsync("AiDetection.StreamRole", "Sub");
        var oldOrientation = await settings.GetAsync("AiDetection.Orientation", "Auto");
        var oldReportIdleDetections = await settings.GetAsync("Detection.ReportIdleDetections", false);
        var oldIdleTimeoutSeconds = await settings.GetAsync("Detection.IdleTimeoutSeconds", 10);
        var oldSnapshotMotionAccuracy = await settings.GetAsync("Detection.SnapshotMotionAccuracy", true);
        var oldRejectMotionJitter = await settings.GetAsync("Detection.RejectMotionJitter", false);
        var oldMotionJitterPixels = await settings.GetAsync("Detection.MotionJitterPixels", 3);
        var oldDepartureGraceSeconds = await settings.GetAsync("Detection.DepartureGraceSeconds", 5);
        var oldSnapshotMarginPercent = await settings.GetAsync("Detection.SnapshotMarginPercent", 12);
        var oldMaxFps = await settings.GetAsync("Detection.MaxFps", 10);
        var oldModelFamily = await settings.GetAsync("Detection.ModelFamily", "Auto");
        var oldDFineWeights = await settings.GetAsync("Detection.DFineWeights", "Obj2Coco");
        var oldDFineTensorRtMode = await settings.GetAsync("Detection.DFineTensorRtMode", "Off");
        var oldYoloXSize = await settings.GetAsync("Detection.YoloXSize", "S");
        var oldAspectMode = await settings.GetAsync("Detection.AspectMode", "Letterbox");
        var oldGpuPreprocessing = await settings.GetAsync("Detection.GpuPreprocessing", false);
        var oldBackend = await settings.GetAsync("Detection.Backend", "BuiltIn");
        var oldExternalUrl = await settings.GetAsync("Detection.ExternalInferenceUrl", "");
        var oldExternalModel = await settings.GetAsync("Detection.ExternalInferenceModel", "");
        var oldExternalInputSize = await settings.GetAsync("Detection.ExternalInferenceInputSize", 640);
        var hadStoredApiKey = !string.IsNullOrEmpty(await settings.GetAsync("Detection.ExternalInferenceApiKey", ""));
        var oldExternalTransport = await settings.GetAsync("Detection.ExternalInferenceTransport", "Auto");

        // Confidence/IoU are genuinely 0-1 fractions everywhere downstream (YOLO-family thresholds) —
        // clamped here so a stray out-of-range value typed into the form can't reach NodeService/the
        // Vision Service pipeline unchecked.
        Confidence = Math.Clamp(Confidence, 0, 1);
        Iou = Math.Clamp(Iou, 0, 1);
        IdleTimeoutSeconds = Math.Max(0, IdleTimeoutSeconds);
        MotionJitterPixels = Math.Clamp(MotionJitterPixels, 1, 15);
        DepartureGraceSeconds = Math.Clamp(DepartureGraceSeconds, 1, 10);
        SnapshotMarginPercent = Math.Clamp(SnapshotMarginPercent, 0, 100);
        MaxFps = Math.Clamp(MaxFps, 0, 60);
        // Only three values are meaningful downstream (CameraDetectionPipeline's switch); anything
        // else lands on "Off" there anyway, so normalize a stray form value rather than store it.
        DFineTensorRtMode = DFineTensorRtMode?.Trim().ToUpperInvariant() switch
        {
            "FP32" => "FP32",
            "FP16" => "FP16",
            _ => "Off",
        };
        Backend = string.Equals(Backend, "ExternalHttp", StringComparison.OrdinalIgnoreCase) ? "ExternalHttp" : "BuiltIn";
        ExternalUrl = NormalizeExternalUrl(ExternalUrl);
        ExternalModel = ExternalModel?.Trim() ?? "";
        // A positive multiple of 32 (InferenceProfile's own requirement); anything else falls back to
        // the D-FINE/YOLOX default rather than being stored.
        ExternalInputSize = ExternalInputSize > 0 && ExternalInputSize % 32 == 0 ? ExternalInputSize : 640;
        // Only four values are meaningful downstream (ExternalInferenceTransportExtensions.Parse);
        // normalize a stray form value to "Auto" rather than store garbage.
        ExternalTransport = ExternalTransport?.Trim() switch
        {
            "Jpeg" => "Jpeg",
            "PixelsYuv420" => "PixelsYuv420",
            "PixelsBgra" => "PixelsBgra",
            _ => "Auto",
        };

        // A configured external backend needs a URL and a model to be usable — reject the save rather
        // than let NodeService silently keep the built-in engine because the fields are half-filled.
        if (Backend == "ExternalHttp" && (string.IsNullOrEmpty(ExternalUrl) || string.IsNullOrEmpty(ExternalModel)))
        {
            var editedUrl = ExternalUrl;
            var editedModel = ExternalModel;
            var editedSize = ExternalInputSize;
            var editedApiKey = ExternalApiKey;
            var editedTransport = ExternalTransport;
            await OnGetAsync();
            // Re-apply the just-submitted values so the form keeps the operator's edits.
            Backend = "ExternalHttp";
            ExternalUrl = editedUrl;
            ExternalModel = editedModel;
            ExternalInputSize = editedSize;
            ExternalApiKey = editedApiKey;
            ExternalTransport = editedTransport;
            SavedMessage = "Enter the external service URL and pick a model (use \"Test connection\" to discover them) before selecting the external backend.";
            StatusIsError = true;
            return Page();
        }

        await settings.SetGlobalAsync("Detection.Confidence", Confidence.ToString("0.####"), by);
        await settings.SetGlobalAsync("Detection.Iou", Iou.ToString("0.####"), by);
        await settings.SetGlobalAsync("AiDetection.StreamRole", StreamRole, by);
        await settings.SetGlobalAsync("AiDetection.Orientation", Orientation, by);
        await settings.SetGlobalAsync("Detection.ReportIdleDetections", ReportIdleDetections.ToString(), by);
        await settings.SetGlobalAsync("Detection.IdleTimeoutSeconds", IdleTimeoutSeconds.ToString(), by);
        await settings.SetGlobalAsync("Detection.SnapshotMotionAccuracy", SnapshotMotionAccuracy.ToString(), by);
        await settings.SetGlobalAsync("Detection.RejectMotionJitter", RejectMotionJitter.ToString(), by);
        await settings.SetGlobalAsync("Detection.MotionJitterPixels", MotionJitterPixels.ToString(), by);
        await settings.SetGlobalAsync("Detection.DepartureGraceSeconds", DepartureGraceSeconds.ToString(), by);
        await settings.SetGlobalAsync("Detection.SnapshotMarginPercent", SnapshotMarginPercent.ToString(), by);
        await settings.SetGlobalAsync("Detection.MaxFps", MaxFps.ToString(), by);
        // RfDetr still renders disabled (no decoder); ModelFamily is "Auto", "DFine" or "YoloX" here.
        await settings.SetGlobalAsync("Detection.ModelFamily", ModelFamily, by);
        await settings.SetGlobalAsync("Detection.DFineWeights", DFineWeights, by);
        await settings.SetGlobalAsync("Detection.DFineTensorRtMode", DFineTensorRtMode, by);
        await settings.SetGlobalAsync("Detection.YoloXSize", YoloXSize, by);
        await settings.SetGlobalAsync("Detection.AspectMode", AspectMode, by);
        await settings.SetGlobalAsync("Detection.GpuPreprocessing", GpuPreprocessing.ToString(), by);
        await settings.SetGlobalAsync("Detection.Backend", Backend, by);
        await settings.SetGlobalAsync("Detection.ExternalInferenceUrl", ExternalUrl ?? "", by);
        await settings.SetGlobalAsync("Detection.ExternalInferenceModel", ExternalModel ?? "", by);
        await settings.SetGlobalAsync("Detection.ExternalInferenceInputSize", ExternalInputSize.ToString(), by);
        await settings.SetGlobalAsync("Detection.ExternalInferenceTransport", ExternalTransport, by);
        // Blank means "unchanged" — the form never re-displays a stored key, so a blank submission
        // only happens when the operator genuinely left it alone (same convention as Email's
        // SmtpPassword/GraphClientSecret). There is no "clear" affordance here, same as those fields.
        var apiKeyChanged = !string.IsNullOrWhiteSpace(ExternalApiKey);
        if (apiKeyChanged)
            await settings.SetGlobalAsync("Detection.ExternalInferenceApiKey", ExternalApiKey!.Trim(), by);

        var details = AuditDiff.Build(
            AuditDiff.Of("Detection.Confidence", oldConfidence.ToString("0.####"), Confidence.ToString("0.####")),
            AuditDiff.Of("Detection.Iou", oldIou.ToString("0.####"), Iou.ToString("0.####")),
            AuditDiff.Of("AiDetection.StreamRole", oldStreamRole, StreamRole),
            AuditDiff.Of("AiDetection.Orientation", oldOrientation, Orientation),
            AuditDiff.Of("Detection.ReportIdleDetections", oldReportIdleDetections.ToString(), ReportIdleDetections.ToString()),
            AuditDiff.Of("Detection.IdleTimeoutSeconds", oldIdleTimeoutSeconds.ToString(), IdleTimeoutSeconds.ToString()),
            AuditDiff.Of("Detection.SnapshotMotionAccuracy", oldSnapshotMotionAccuracy.ToString(), SnapshotMotionAccuracy.ToString()),
            AuditDiff.Of("Detection.RejectMotionJitter", oldRejectMotionJitter.ToString(), RejectMotionJitter.ToString()),
            AuditDiff.Of("Detection.MotionJitterPixels", oldMotionJitterPixels.ToString(), MotionJitterPixels.ToString()),
            AuditDiff.Of("Detection.DepartureGraceSeconds", oldDepartureGraceSeconds.ToString(), DepartureGraceSeconds.ToString()),
            AuditDiff.Of("Detection.SnapshotMarginPercent", oldSnapshotMarginPercent.ToString(), SnapshotMarginPercent.ToString()),
            AuditDiff.Of("Detection.MaxFps", oldMaxFps.ToString(), MaxFps.ToString()),
            AuditDiff.Of("Detection.ModelFamily", oldModelFamily, ModelFamily),
            AuditDiff.Of("Detection.DFineWeights", oldDFineWeights, DFineWeights),
            AuditDiff.Of("Detection.DFineTensorRtMode", oldDFineTensorRtMode, DFineTensorRtMode),
            AuditDiff.Of("Detection.YoloXSize", oldYoloXSize, YoloXSize),
            AuditDiff.Of("Detection.AspectMode", oldAspectMode, AspectMode),
            AuditDiff.Of("Detection.GpuPreprocessing", oldGpuPreprocessing.ToString(), GpuPreprocessing.ToString()),
            AuditDiff.Of("Detection.Backend", oldBackend, Backend),
            AuditDiff.Of("Detection.ExternalInferenceUrl", oldExternalUrl, ExternalUrl ?? ""),
            AuditDiff.Of("Detection.ExternalInferenceModel", oldExternalModel, ExternalModel ?? ""),
            AuditDiff.Of("Detection.ExternalInferenceInputSize", oldExternalInputSize.ToString(), ExternalInputSize.ToString()),
            AuditDiff.Of("Detection.ExternalInferenceTransport", oldExternalTransport, ExternalTransport),
            AuditDiff.SecretChanged("Detection.ExternalInferenceApiKey", apiKeyChanged));

        await auditService.LogAsync("Settings.Update",
            CurrentUserId, by,
            HttpContext.Connection.RemoteIpAddress?.ToString(), details);

        // So the form's "unchanged" placeholder reflects a key just saved this request.
        HasStoredExternalApiKey = hadStoredApiKey || apiKeyChanged;
        ExternalApiKey = null;
        SavedMessage = "Saved.";
        return Page();
    }

    /// <summary>"Test connection" — probes the entered (unsaved) service for /healthz + /v1/models so
    /// the operator can confirm it is reachable and pick a model. Mirrors Email's OnPostTestSendAsync
    /// and Cameras/Edit's OnPostProbeAsync: try/catch into a message + audit entry, never throws.</summary>
    public async Task<IActionResult> OnPostTestConnectionAsync(CancellationToken ct)
    {
        var by = User.Identity?.Name;

        // The external fields are model-bound from the form; everything else on the page should show
        // its saved value, so load those and then restore the operator's in-progress external edits.
        var editedUrl = NormalizeExternalUrl(ExternalUrl);
        var editedModel = ExternalModel?.Trim() ?? "";
        // Blank means "use the already-saved key" — same "unchanged" convention the field uses on
        // Save, so testing right after typing a new key (not yet saved) still authenticates with it.
        var editedApiKey = string.IsNullOrWhiteSpace(ExternalApiKey) ? null : ExternalApiKey.Trim();
        await OnGetAsync();
        Backend = "ExternalHttp";
        ExternalUrl = editedUrl;
        ExternalModel = editedModel;
        var url = editedUrl;
        var apiKey = editedApiKey ?? await settings.GetAsync("Detection.ExternalInferenceApiKey", "");

        if (string.IsNullOrEmpty(url))
        {
            SavedMessage = "Enter the external service URL first (including its port, e.g. http://192.168.1.50:8080).";
            StatusIsError = true;
            return Page();
        }

        var result = await externalProbe.ProbeAsync(url, apiKey, ct);
        ProbeHealth = result.Health;

        if (result.Error is not null)
        {
            await auditService.LogAsync("Settings.DetectionExternalTestFailed", CurrentUserId, by,
                HttpContext.Connection.RemoteIpAddress?.ToString(), $"{url}: {result.Error}");
            SavedMessage = result.Error;
            StatusIsError = true;
            return Page();
        }

        AvailableModels = result.Models ?? [];
        // Keep the previously-chosen model selected if the service still offers it; otherwise take
        // the first one so the picklist and the hidden input-size field are never left inconsistent.
        if (AvailableModels.All(m => !string.Equals(m.Name, ExternalModel, StringComparison.Ordinal)))
            ExternalModel = AvailableModels[0].Name;
        ExternalInputSize = AvailableModels.First(m => m.Name == ExternalModel).InputSize;

        await auditService.LogAsync("Settings.DetectionExternalTestSucceeded", CurrentUserId, by,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            $"{url}: {AvailableModels.Count} model(s) — {string.Join(", ", AvailableModels.Select(m => m.Name))}");
        SavedMessage = $"Connected. {AvailableModels.Count} model(s) available — pick one and Save.";
        StatusIsError = false;
        return Page();
    }

    /// <summary>Trim, drop a trailing slash, and require an absolute http(s) URL — the same shape
    /// LiveView's PublicOrigin validation uses. Returns "" for blank or invalid input; the caller
    /// decides whether that is an error in context.</summary>
    private static string NormalizeExternalUrl(string? raw)
    {
        var trimmed = raw?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(trimmed)) return "";
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? trimmed : "";
    }
}
