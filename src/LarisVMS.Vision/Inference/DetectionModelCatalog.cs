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
    public static string GetFileName(DetectionModelFamily family, DFineWeights dfineWeights) => family switch
    {
        DetectionModelFamily.DFine => dfineWeights switch
        {
            DFineWeights.Obj2Coco => "dfine_s_obj2coco.onnx",
            DFineWeights.Obj365 => "dfine_s_obj365.onnx",
            _ => throw new ArgumentOutOfRangeException(nameof(dfineWeights), dfineWeights, null),
        },
        DetectionModelFamily.RfDetr => throw new NotSupportedException(
            "RF-DETR is not yet implemented — deferred scope, see the model-swap plan."),
        DetectionModelFamily.YoloX => throw new NotSupportedException(
            "YOLOX is not yet implemented — deferred scope, see the model-swap plan."),
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, null),
    };

    /// <summary>The label table index-matching a D-FINE variant's own `logits` output — see
    /// DFineLabels' own doc comment for where these came from.</summary>
    public static IReadOnlyList<string> GetDFineLabels(DFineWeights dfineWeights) => dfineWeights switch
    {
        DFineWeights.Obj2Coco => DFineLabels.Obj2Coco,
        DFineWeights.Obj365 => DFineLabels.Obj365,
        _ => throw new ArgumentOutOfRangeException(nameof(dfineWeights), dfineWeights, null),
    };
}
