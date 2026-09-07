using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Admin.Settings;

/// <summary>Global AI-detection defaults — the base of the Camera &rarr; Node &rarr; Global chain a
/// per-camera override on Cameras/Edit falls back to for Confidence/IoU/StreamRole. Split into its
/// own tab the same way Recording's own settings were, since none of these previously had any admin
/// UI at all (the Setting rows existed and were already read by NodeService, just never written from
/// anywhere).</summary>
[Authorize("Settings.Edit")]
public class DetectionModel(ISettingsResolver settings, IAuditService auditService) : PageModel
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
    /// <summary>When on (the default), the Vision Service rejects raw-detection-box jitter so a
    /// parked vehicle stops flickering to "moving", and finalizes a detection's snapshot promptly
    /// once its object leaves frame instead of holding the span open long enough for a later,
    /// unrelated object of the same type to be merged into it. Off restores the previous behaviour
    /// for side-by-side comparison. Global; changing it restarts each camera's detection pipeline.</summary>
    [BindProperty] public bool SnapshotMotionAccuracy { get; set; } = true;
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
        SnapshotMotionAccuracy = await settings.GetAsync("Detection.SnapshotMotionAccuracy", true);
        MaxFps = await settings.GetAsync("Detection.MaxFps", 10);
        ModelFamily = await settings.GetAsync("Detection.ModelFamily", "Auto");
        DFineWeights = await settings.GetAsync("Detection.DFineWeights", "Obj2Coco");
        DFineTensorRtMode = await settings.GetAsync("Detection.DFineTensorRtMode", "Off");
        YoloXSize = await settings.GetAsync("Detection.YoloXSize", "S");
        AspectMode = await settings.GetAsync("Detection.AspectMode", "Letterbox");
        GpuPreprocessing = await settings.GetAsync("Detection.GpuPreprocessing", false);
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
        var oldSnapshotMotionAccuracy = await settings.GetAsync("Detection.SnapshotMotionAccuracy", true);
        var oldMaxFps = await settings.GetAsync("Detection.MaxFps", 10);
        var oldModelFamily = await settings.GetAsync("Detection.ModelFamily", "Auto");
        var oldDFineWeights = await settings.GetAsync("Detection.DFineWeights", "Obj2Coco");
        var oldDFineTensorRtMode = await settings.GetAsync("Detection.DFineTensorRtMode", "Off");
        var oldYoloXSize = await settings.GetAsync("Detection.YoloXSize", "S");
        var oldAspectMode = await settings.GetAsync("Detection.AspectMode", "Letterbox");
        var oldGpuPreprocessing = await settings.GetAsync("Detection.GpuPreprocessing", false);

        // Confidence/IoU are genuinely 0-1 fractions everywhere downstream (YOLO-family thresholds) —
        // clamped here so a stray out-of-range value typed into the form can't reach NodeService/the
        // Vision Service pipeline unchecked.
        Confidence = Math.Clamp(Confidence, 0, 1);
        Iou = Math.Clamp(Iou, 0, 1);
        IdleTimeoutSeconds = Math.Max(0, IdleTimeoutSeconds);
        MaxFps = Math.Clamp(MaxFps, 0, 60);
        // Only three values are meaningful downstream (CameraDetectionPipeline's switch); anything
        // else lands on "Off" there anyway, so normalize a stray form value rather than store it.
        DFineTensorRtMode = DFineTensorRtMode?.Trim().ToUpperInvariant() switch
        {
            "FP32" => "FP32",
            "FP16" => "FP16",
            _ => "Off",
        };

        await settings.SetGlobalAsync("Detection.Confidence", Confidence.ToString("0.####"), by);
        await settings.SetGlobalAsync("Detection.Iou", Iou.ToString("0.####"), by);
        await settings.SetGlobalAsync("AiDetection.StreamRole", StreamRole, by);
        await settings.SetGlobalAsync("AiDetection.Orientation", Orientation, by);
        await settings.SetGlobalAsync("Detection.ReportIdleDetections", ReportIdleDetections.ToString(), by);
        await settings.SetGlobalAsync("Detection.IdleTimeoutSeconds", IdleTimeoutSeconds.ToString(), by);
        await settings.SetGlobalAsync("Detection.SnapshotMotionAccuracy", SnapshotMotionAccuracy.ToString(), by);
        await settings.SetGlobalAsync("Detection.MaxFps", MaxFps.ToString(), by);
        // RfDetr still renders disabled (no decoder); ModelFamily is "Auto", "DFine" or "YoloX" here.
        await settings.SetGlobalAsync("Detection.ModelFamily", ModelFamily, by);
        await settings.SetGlobalAsync("Detection.DFineWeights", DFineWeights, by);
        await settings.SetGlobalAsync("Detection.DFineTensorRtMode", DFineTensorRtMode, by);
        await settings.SetGlobalAsync("Detection.YoloXSize", YoloXSize, by);
        await settings.SetGlobalAsync("Detection.AspectMode", AspectMode, by);
        await settings.SetGlobalAsync("Detection.GpuPreprocessing", GpuPreprocessing.ToString(), by);

        var details = AuditDiff.Build(
            AuditDiff.Of("Detection.Confidence", oldConfidence.ToString("0.####"), Confidence.ToString("0.####")),
            AuditDiff.Of("Detection.Iou", oldIou.ToString("0.####"), Iou.ToString("0.####")),
            AuditDiff.Of("AiDetection.StreamRole", oldStreamRole, StreamRole),
            AuditDiff.Of("AiDetection.Orientation", oldOrientation, Orientation),
            AuditDiff.Of("Detection.ReportIdleDetections", oldReportIdleDetections.ToString(), ReportIdleDetections.ToString()),
            AuditDiff.Of("Detection.IdleTimeoutSeconds", oldIdleTimeoutSeconds.ToString(), IdleTimeoutSeconds.ToString()),
            AuditDiff.Of("Detection.SnapshotMotionAccuracy", oldSnapshotMotionAccuracy.ToString(), SnapshotMotionAccuracy.ToString()),
            AuditDiff.Of("Detection.MaxFps", oldMaxFps.ToString(), MaxFps.ToString()),
            AuditDiff.Of("Detection.ModelFamily", oldModelFamily, ModelFamily),
            AuditDiff.Of("Detection.DFineWeights", oldDFineWeights, DFineWeights),
            AuditDiff.Of("Detection.DFineTensorRtMode", oldDFineTensorRtMode, DFineTensorRtMode),
            AuditDiff.Of("Detection.YoloXSize", oldYoloXSize, YoloXSize),
            AuditDiff.Of("Detection.AspectMode", oldAspectMode, AspectMode),
            AuditDiff.Of("Detection.GpuPreprocessing", oldGpuPreprocessing.ToString(), GpuPreprocessing.ToString()));

        await auditService.LogAsync("Settings.Update",
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, by,
            HttpContext.Connection.RemoteIpAddress?.ToString(), details);

        SavedMessage = "Saved.";
        return Page();
    }
}
