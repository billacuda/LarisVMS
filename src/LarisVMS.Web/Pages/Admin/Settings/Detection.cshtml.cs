using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core;
using LarisVMS.Core.Interfaces;
using LarisVMS.Core.Security;

namespace LarisVMS.Web.Pages.Admin.Settings;

/// <summary>Global AI-detection defaults — the base of the Camera &rarr; Node &rarr; Global chain a
/// per-camera override on Cameras/Edit falls back to for Confidence/IoU/StreamRole. Split into its
/// own tab the same way Recording's own settings were, since none of these previously had any admin
/// UI at all (the Setting rows existed and were already read by NodeService, just never written from
/// anywhere).</summary>
[Authorize("Settings.Edit")]
public class DetectionModel(ISettingsResolver settings, IAuditService auditService,
    INodeService nodeService, IHttpClientFactory httpFactory) : PageModel
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
    /// <summary>Node-scoped ceiling on frames/sec per camera reaching the model. The Vision Service
    /// still decodes the Sub stream in real time; an ffmpeg fps= filter drops the surplus before
    /// inference so the GPU idles between frames. 0 = no cap. 10 is plenty for object tracking.</summary>
    [BindProperty] public int MaxFps { get; set; } = 10;
    /// <summary>Node-scoped, not this page's other global-default fields' Camera-override sibling —
    /// see NodeConfigResponse.DetectionModelFamily's own doc comment for why. Auto → YOLOX; DFine is
    /// selectable but opt-in; RF-DETR still renders disabled (no decoder).</summary>
    [BindProperty] public string ModelFamily { get; set; } = "Auto";
    [BindProperty] public string DFineWeights { get; set; } = "Obj2Coco";
    /// <summary>Node-scoped like ModelFamily — the YOLOX model size (Nano/Tiny/S/M/L/X). Only
    /// meaningful when the family resolves to YOLOX; the node fetches the chosen ONNX from the server
    /// on first use (YOLOX models aren't bundled).</summary>
    [BindProperty] public string YoloXSize { get; set; } = "S";
    /// <summary>Detection/hardware-acceleration overhaul, pass 1 — node-scoped like ModelFamily
    /// above (Admin/Nodes carries the per-node override), not this page's other global-default
    /// fields' Camera-override sibling. Only "Letterbox"/"Stretch" are implemented; AspectMatched
    /// is reserved for a future pass and never offered here.</summary>
    [BindProperty] public string AspectMode { get; set; } = "Letterbox";
    /// <summary>Detection/hardware-acceleration overhaul, pass 3b — node-scoped like AspectMode
    /// above, opt-in and off by default: meaningfully more CPU/GPU work than the continuous
    /// Sub-stream pipeline alone, so an admin turns it on deliberately per deployment rather than it
    /// silently starting the moment a build that supports it ships.</summary>
    [BindProperty] public bool EnableHighResReDetection { get; set; }
    /// <summary>Diagnostic-only, node-scoped. When on, Vision Service writes one cropped JPEG per
    /// re-detection trigger to each node's logs\vision-debug\ folder — useful for confirming
    /// snapshot box alignment, at a storage cost with no automatic cleanup (hence the purge button).
    /// Off by default for new installs.</summary>
    [BindProperty] public bool EnableVisionDebugImages { get; set; }
    /// <summary>Pass 4a — node-scoped, opt-in. Moves per-frame colour conversion + normalize off the
    /// CPU onto the accelerator (an ONNX preprocessing head that runs on CUDA/DirectML/OpenVINO
    /// alike). Restarts each detection pipeline when toggled.</summary>
    [BindProperty] public bool GpuPreprocessing { get; set; }
    /// <summary>Pass F — node-scoped, opt-in. Decodes each Sub stream at up to its native resolution
    /// (long edge capped to 1280) so the eager AI-detection snapshot is cropped from a sharper frame.
    /// Only helps where the Sub stream itself is bigger than the detector input; small extra CPU +
    /// memory, no extra GPU. Forces GPU preprocessing off per camera while on. Restarts pipelines.</summary>
    [BindProperty] public bool HiResSnapshots { get; set; }

    public string? SavedMessage { get; set; }
    public bool StatusIsError { get; set; }

    public async Task OnGetAsync()
    {
        Confidence = await settings.GetAsync("Detection.Confidence", 0.35);
        Iou = await settings.GetAsync("Detection.Iou", 0.5);
        StreamRole = await settings.GetAsync("AiDetection.StreamRole", "Sub");
        Orientation = await settings.GetAsync("AiDetection.Orientation", "Auto");
        ReportIdleDetections = await settings.GetAsync("Detection.ReportIdleDetections", false);
        IdleTimeoutSeconds = await settings.GetAsync("Detection.IdleTimeoutSeconds", 10);
        MaxFps = await settings.GetAsync("Detection.MaxFps", 10);
        ModelFamily = await settings.GetAsync("Detection.ModelFamily", "Auto");
        DFineWeights = await settings.GetAsync("Detection.DFineWeights", "Obj2Coco");
        YoloXSize = await settings.GetAsync("Detection.YoloXSize", "S");
        AspectMode = await settings.GetAsync("Detection.AspectMode", "Letterbox");
        EnableHighResReDetection = await settings.GetAsync("Detection.EnableHighResReDetection", false);
        EnableVisionDebugImages = await settings.GetAsync("Detection.EnableVisionDebugImages", false);
        GpuPreprocessing = await settings.GetAsync("Detection.GpuPreprocessing", false);
        HiResSnapshots = await settings.GetAsync("Detection.HiResSnapshots", false);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var by = User.Identity?.Name;

        var oldConfidence = await settings.GetAsync("Detection.Confidence", 0.35);
        var oldIou = await settings.GetAsync("Detection.Iou", 0.5);
        var oldStreamRole = await settings.GetAsync("AiDetection.StreamRole", "Sub");
        var oldOrientation = await settings.GetAsync("AiDetection.Orientation", "Auto");
        var oldReportIdleDetections = await settings.GetAsync("Detection.ReportIdleDetections", false);
        var oldIdleTimeoutSeconds = await settings.GetAsync("Detection.IdleTimeoutSeconds", 10);
        var oldMaxFps = await settings.GetAsync("Detection.MaxFps", 10);
        var oldModelFamily = await settings.GetAsync("Detection.ModelFamily", "Auto");
        var oldDFineWeights = await settings.GetAsync("Detection.DFineWeights", "Obj2Coco");
        var oldYoloXSize = await settings.GetAsync("Detection.YoloXSize", "S");
        var oldAspectMode = await settings.GetAsync("Detection.AspectMode", "Letterbox");
        var oldEnableHighResReDetection = await settings.GetAsync("Detection.EnableHighResReDetection", false);
        var oldEnableVisionDebugImages = await settings.GetAsync("Detection.EnableVisionDebugImages", false);
        var oldGpuPreprocessing = await settings.GetAsync("Detection.GpuPreprocessing", false);
        var oldHiResSnapshots = await settings.GetAsync("Detection.HiResSnapshots", false);

        // Confidence/IoU are genuinely 0-1 fractions everywhere downstream (YOLO-family thresholds) —
        // clamped here so a stray out-of-range value typed into the form can't reach NodeService/the
        // Vision Service pipeline unchecked.
        Confidence = Math.Clamp(Confidence, 0, 1);
        Iou = Math.Clamp(Iou, 0, 1);
        IdleTimeoutSeconds = Math.Max(0, IdleTimeoutSeconds);
        MaxFps = Math.Clamp(MaxFps, 0, 60);

        await settings.SetGlobalAsync("Detection.Confidence", Confidence.ToString("0.####"), by);
        await settings.SetGlobalAsync("Detection.Iou", Iou.ToString("0.####"), by);
        await settings.SetGlobalAsync("AiDetection.StreamRole", StreamRole, by);
        await settings.SetGlobalAsync("AiDetection.Orientation", Orientation, by);
        await settings.SetGlobalAsync("Detection.ReportIdleDetections", ReportIdleDetections.ToString(), by);
        await settings.SetGlobalAsync("Detection.IdleTimeoutSeconds", IdleTimeoutSeconds.ToString(), by);
        await settings.SetGlobalAsync("Detection.MaxFps", MaxFps.ToString(), by);
        // RfDetr still renders disabled (no decoder); ModelFamily is "Auto", "DFine" or "YoloX" here.
        await settings.SetGlobalAsync("Detection.ModelFamily", ModelFamily, by);
        await settings.SetGlobalAsync("Detection.DFineWeights", DFineWeights, by);
        await settings.SetGlobalAsync("Detection.YoloXSize", YoloXSize, by);
        await settings.SetGlobalAsync("Detection.AspectMode", AspectMode, by);
        await settings.SetGlobalAsync("Detection.EnableHighResReDetection", EnableHighResReDetection.ToString(), by);
        await settings.SetGlobalAsync("Detection.EnableVisionDebugImages", EnableVisionDebugImages.ToString(), by);
        await settings.SetGlobalAsync("Detection.GpuPreprocessing", GpuPreprocessing.ToString(), by);
        await settings.SetGlobalAsync("Detection.HiResSnapshots", HiResSnapshots.ToString(), by);

        var details = AuditDiff.Build(
            AuditDiff.Of("Detection.Confidence", oldConfidence.ToString("0.####"), Confidence.ToString("0.####")),
            AuditDiff.Of("Detection.Iou", oldIou.ToString("0.####"), Iou.ToString("0.####")),
            AuditDiff.Of("AiDetection.StreamRole", oldStreamRole, StreamRole),
            AuditDiff.Of("AiDetection.Orientation", oldOrientation, Orientation),
            AuditDiff.Of("Detection.ReportIdleDetections", oldReportIdleDetections.ToString(), ReportIdleDetections.ToString()),
            AuditDiff.Of("Detection.IdleTimeoutSeconds", oldIdleTimeoutSeconds.ToString(), IdleTimeoutSeconds.ToString()),
            AuditDiff.Of("Detection.MaxFps", oldMaxFps.ToString(), MaxFps.ToString()),
            AuditDiff.Of("Detection.ModelFamily", oldModelFamily, ModelFamily),
            AuditDiff.Of("Detection.DFineWeights", oldDFineWeights, DFineWeights),
            AuditDiff.Of("Detection.YoloXSize", oldYoloXSize, YoloXSize),
            AuditDiff.Of("Detection.AspectMode", oldAspectMode, AspectMode),
            AuditDiff.Of("Detection.EnableHighResReDetection", oldEnableHighResReDetection.ToString(), EnableHighResReDetection.ToString()),
            AuditDiff.Of("Detection.EnableVisionDebugImages", oldEnableVisionDebugImages.ToString(), EnableVisionDebugImages.ToString()),
            AuditDiff.Of("Detection.GpuPreprocessing", oldGpuPreprocessing.ToString(), GpuPreprocessing.ToString()),
            AuditDiff.Of("Detection.HiResSnapshots", oldHiResSnapshots.ToString(), HiResSnapshots.ToString()));

        await auditService.LogAsync("Settings.Update",
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, by,
            HttpContext.Connection.RemoteIpAddress?.ToString(), details);

        SavedMessage = "Saved.";
        return Page();
    }

    /// <summary>Deletes the vision-debug JPEGs already written to every node's logs\vision-debug\
    /// folder. Fanned out to each node the same proxied, per-node-signed-token way
    /// Admin/Nodes' "Restart service" button reaches a node — the browser never talks to a node
    /// directly. Best-effort per node; the summary reports what each one did.</summary>
    public async Task<IActionResult> OnPostPurgeVisionDebugAsync()
    {
        await OnGetAsync(); // repopulate the form fields for the re-render

        var nodes = await nodeService.ListAsync();
        var reachable = nodes
            .Where(n => n is { LastIpAddress: not null, LivePort: not null, MediaSigningKey: not null })
            .ToList();

        if (reachable.Count == 0)
        {
            SavedMessage = "No nodes have reported an address yet — nothing to purge.";
            StatusIsError = true;
            return Page();
        }

        var client = httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        var totalDeleted = 0;
        var failures = new List<string>();
        foreach (var node in reachable)
        {
            var token = MediaToken.IssueForNodeControl("purge-vision-debug", node.MediaSigningKey!, TimeSpan.FromSeconds(30));
            try
            {
                var response = await client.SendAsync(MediaTokenRequest.Create(HttpMethod.Post,
                    $"http://{node.LastIpAddress}:{node.LivePort}/purge-vision-debug?token={Uri.EscapeDataString(token)}", token));
                var body = (await response.Content.ReadAsStringAsync()).Trim();
                if (response.IsSuccessStatusCode && int.TryParse(body, out var deleted))
                    totalDeleted += deleted;
                else
                    failures.Add($"{node.Name}: {(response.IsSuccessStatusCode ? "unexpected response" : $"HTTP {(int)response.StatusCode}")}{(string.IsNullOrEmpty(body) ? "" : $" ({body})")}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"{node.Name}: {ex.Message}");
            }
        }

        SavedMessage = failures.Count == 0
            ? $"Purged {totalDeleted} vision-debug image(s) across {reachable.Count} node(s)."
            : $"Purged {totalDeleted} image(s); {failures.Count} node(s) failed: {string.Join("; ", failures)}";
        StatusIsError = failures.Count > 0;

        await auditService.LogAsync("Settings.Update",
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, User.Identity?.Name,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            $"Purged vision-debug images: {totalDeleted} across {reachable.Count} node(s)");

        return Page();
    }
}
