namespace LarisVMS.Core.Enums;

/// <summary>Which pretrained D-FINE weight variant is loaded — both are the "small" size (10.7M
/// params) fetched by tools/export-models/fetch_dfine.py, differing only in training data /
/// class vocabulary. See DFineLabels for each variant's exact label table.</summary>
public enum DFineWeights
{
    /// <summary>Trained on Objects365 then fine-tuned on COCO — 80 classes (DFineLabels.Obj2Coco),
    /// the same vocabulary CocoCategoryMap already resolves. Smaller blast radius than Obj365, and
    /// the default: an existing deployment's category filters/badges keep working unchanged.</summary>
    Obj2Coco = 0,

    /// <summary>Trained directly on Objects365 — 366 classes (DFineLabels.Obj365), a much richer
    /// vocabulary (e.g. "SUV", "Wild Bird", "Rickshaw") at the cost of needing the broader
    /// CocoCategoryMap.VehicleClasses/AnimalClasses coverage this integration adds.</summary>
    Obj365 = 1,
}
