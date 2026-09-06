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
    /// <summary>Auto resolves to YOLOX on every accelerator — it runs well on low-power and
    /// non-Nvidia GPUs, uses an Nvidia GPU fully where present (same ONNX Runtime EP path D-FINE
    /// uses), needs no letterbox, and ByteTrack was designed against it. D-FINE stays selectable but
    /// is experimental (unpredictable results; broken across Slice tile seams — see IDEAS.md).
    /// <paramref name="accelerator"/> no longer influences the choice; kept on the
    /// signature for callers and for a future family that might key off it.</summary>
    public static DetectionModelFamily Choose(DetectionModelFamily desired, AiAccelerator accelerator)
    {
        _ = accelerator;
        var resolved = desired == DetectionModelFamily.Auto ? DetectionModelFamily.YoloX : desired;

        // RF-DETR still has no decoder (DetectionEngineFactory would throw). YOLOX ships now, so it
        // is no longer in this fallback. Remove RF-DETR too once it lands.
        return resolved is DetectionModelFamily.RfDetr ? DetectionModelFamily.DFine : resolved;
    }
}
