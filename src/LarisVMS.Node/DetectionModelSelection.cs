using LarisVMS.Core.Enums;

namespace LarisVMS.Node;

/// <summary>
/// Resolves a node's desired Detection.ModelFamily (delivered node-side via
/// NodeConfigResponse.DetectionModelFamily) against the accelerator AccelSelection.Choose already
/// resolved for this node — mirrors that class's own shape exactly: a small, pure,
/// priority-ordered choice with no I/O of its own.
/// </summary>
public static class DetectionModelSelection
{
    /// <summary>Auto's own hardware-based default: Nvidia gets D-FINE (GPU-friendly, DETR-style —
    /// see the model-swap plan's own reasoning), everything else gets YOLOX. RF-DETR is never an
    /// Auto default — it's a selectable alternative only.</summary>
    public static DetectionModelFamily Choose(DetectionModelFamily desired, AiAccelerator accelerator)
    {
        var resolved = desired == DetectionModelFamily.Auto
            ? (accelerator == AiAccelerator.Nvidia ? DetectionModelFamily.DFine : DetectionModelFamily.YoloX)
            : desired;

        // RF-DETR and YOLOX have no working decoder yet (DetectionEngineFactory would throw
        // constructing either) — fall back to D-FINE, the one family actually implemented, so a
        // node keeps detecting instead of crashing its Vision Service pipeline. The admin picker
        // already renders both disabled, so this only ever matters for Auto's own Intel/AMD/CPU
        // default (which would otherwise pick YOLOX) or a stale/manually-set setting value. Remove
        // this fallback once either family actually ships.
        return resolved is DetectionModelFamily.RfDetr or DetectionModelFamily.YoloX
            ? DetectionModelFamily.DFine
            : resolved;
    }
}
