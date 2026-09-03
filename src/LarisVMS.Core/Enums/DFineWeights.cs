namespace LarisVMS.Core.Enums;

/// <summary>Which pretrained D-FINE variant is loaded, fetched by tools/export-models/fetch_dfine.py.
/// The name encodes both the training data / class vocabulary and the model size — every variant
/// runs the exact same pipeline (640x640 input, 1/255 rescale, no normalize, `logits`/`pred_boxes`
/// output, DFineDecoder), differing only in backbone weight and its label table. See DFineLabels for
/// each vocabulary's exact table.</summary>
public enum DFineWeights
{
    /// <summary>Small (10.7M params). Trained on Objects365 then fine-tuned on COCO — 80 classes
    /// (DFineLabels.Obj2Coco), the same vocabulary CocoCategoryMap already resolves. Smaller blast
    /// radius than Obj365, and the default: an existing deployment's category filters/badges keep
    /// working unchanged.</summary>
    Obj2Coco = 0,

    /// <summary>Small (10.7M params). Trained directly on Objects365 — 366 classes
    /// (DFineLabels.Obj365), a much richer vocabulary (e.g. "SUV", "Wild Bird", "Rickshaw") at the
    /// cost of needing the broader CocoCategoryMap.VehicleClasses/AnimalClasses coverage this
    /// integration adds.</summary>
    Obj365 = 1,

    /// <summary>Medium (19.6M params). Same COCO-80 vocabulary as <see cref="Obj2Coco"/>
    /// (DFineLabels.Obj2Coco) — a heavier backbone for better accuracy at a higher per-frame cost, a
    /// drop-in swap for the small obj2coco model with no downstream vocabulary change. Upstream
    /// ustc-community/dfine-medium-obj2coco, ONNX via onnx-community/dfine_m_obj2coco-ONNX.</summary>
    Obj2CocoMedium = 2,
}
