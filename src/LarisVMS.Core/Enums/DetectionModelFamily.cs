namespace LarisVMS.Core.Enums;

/// <summary>
/// Which detection model architecture actually runs inference — a policy choice (Setting +
/// SettingOverride(Scope.Node), resolved the same Node &rarr; Global chain AiAccelerator's sibling
/// settings use), not a fact about specific hardware the way AiAccelerator is. Node-scoped rather
/// than per-camera: one Vision Service process serves every camera on a node from the same loaded
/// model, so which model family that process uses is inherently a per-node choice.
///
/// RfDetr and YoloX are declared now (the picker slot the model-swap scoping asked for) but have no
/// working decoder yet — DetectionEngineFactory throws a clear "not yet implemented" for either
/// rather than silently falling back, and DetectionModelSelection.Choose's Auto path never resolves
/// to them until they're built. Adding real support later is "implement a decoder + un-disable an
/// admin-picker option," not an enum/schema migration.
/// </summary>
public enum DetectionModelFamily
{
    /// <summary>Nvidia -&gt; DFine, everything else -&gt; DFine too for now (YoloX, the eventual
    /// Intel/CPU default, isn't implemented yet — see DetectionModelSelection.Choose's own doc
    /// comment for the fallback this implies).</summary>
    Auto = 0,
    DFine = 1,
    RfDetr = 2,
    YoloX = 3,
}
