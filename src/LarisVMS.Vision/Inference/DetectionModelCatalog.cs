using LarisVMS.Core.Enums;

namespace LarisVMS.Vision.Inference;

/// <summary>
/// Exact filenames tools/export-models/fetch_dfine.py writes into the models directory, and the
/// label table that goes with each — CameraPipelineManager.ResolveModelPath looks these up by
/// (family, weights) instead of alphabetically globbing whatever .onnx happens to sort first, which
/// is what let YOLOv9 keep silently running even after this integration replaced it (a stale bundled
/// file would have kept winning the glob). fetch_dfine.py must rename its downloads to match these
/// exactly — every onnx-community repo names its own export the same generic "onnx/model.onnx".
/// </summary>
public static class DetectionModelCatalog
{
    public static string GetFileName(DetectionModelFamily family, DFineWeights dfineWeights, YoloXSize yoloXSize) => family switch
    {
        DetectionModelFamily.DFine => dfineWeights switch
        {
            DFineWeights.Obj2Coco => "dfine_s_obj2coco.onnx",
            DFineWeights.Obj365 => "dfine_s_obj365.onnx",
            DFineWeights.Obj2CocoMedium => "dfine_m_obj2coco.onnx",
            _ => throw new ArgumentOutOfRangeException(nameof(dfineWeights), dfineWeights, null),
        },
        DetectionModelFamily.YoloX => GetYoloXFileName(yoloXSize),
        DetectionModelFamily.RfDetr => throw new NotSupportedException(
            "RF-DETR is not yet implemented — deferred scope, see the model-swap plan."),
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, null),
    };

    /// <summary>Filenames tools/export-models/fetch_yolox.py writes, and that the server's model
    /// cache / node fetch use — one decode-baked ONNX per size.</summary>
    public static string GetYoloXFileName(YoloXSize size) => size switch
    {
        YoloXSize.Nano => "yolox_nano.onnx",
        YoloXSize.Tiny => "yolox_tiny.onnx",
        YoloXSize.S => "yolox_s.onnx",
        YoloXSize.M => "yolox_m.onnx",
        YoloXSize.L => "yolox_l.onnx",
        YoloXSize.X => "yolox_x.onnx",
        _ => throw new ArgumentOutOfRangeException(nameof(size), size, null),
    };

    /// <summary>The label table index-matching a D-FINE variant's own `logits` output — see
    /// DFineLabels' own doc comment for where these came from.</summary>
    public static IReadOnlyList<string> GetDFineLabels(DFineWeights dfineWeights) => dfineWeights switch
    {
        DFineWeights.Obj2Coco => DFineLabels.Obj2Coco,
        DFineWeights.Obj365 => DFineLabels.Obj365,
        DFineWeights.Obj2CocoMedium => DFineLabels.Obj2Coco, // same COCO-80 vocabulary as the small obj2coco model
        _ => throw new ArgumentOutOfRangeException(nameof(dfineWeights), dfineWeights, null),
    };

    /// <summary>YOLOX is COCO-80 for every size — see <see cref="DFineLabels.YoloXCoco"/>.</summary>
    public static IReadOnlyList<string> GetYoloXLabels() => DFineLabels.YoloXCoco;

    /// <summary>The square network input size Megvii ships each size at (Nano/Tiny at 416, the rest
    /// at 640 — both multiples of 32). This is the <c>networkSize</c> for
    /// <see cref="InferenceProfile.Create"/> and the long-edge target for AspectFit (pass F). Must
    /// match the pinned ONNX export's fixed input dims — <see cref="YoloXEngine"/> validates it.</summary>
    public static int GetYoloXNetworkSize(YoloXSize size) => size is YoloXSize.Nano or YoloXSize.Tiny ? 416 : 640;
}
