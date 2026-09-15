namespace LarisVMS.Core.Enums;

/// <summary>
/// Which detection model architecture actually runs inference — a policy choice (Setting +
/// SettingOverride(Scope.Node), resolved the same Node &rarr; Global chain AiAccelerator's sibling
/// settings use), not a fact about specific hardware the way AiAccelerator is. Node-scoped rather
/// than per-camera: one Vision Service process serves every camera on a node from the same loaded
/// model, so which model family that process uses is inherently a per-node choice.
///
/// YoloX is the Auto default on every accelerator. DFine is selectable but experimental. RfDetr is
/// still declared-only — DetectionEngineFactory throws "not yet implemented" and
/// DetectionModelSelection.Choose falls it back to DFine. Adding RF-DETR later is "implement a
/// decoder + un-disable an admin-picker option," not an enum/schema migration.
///
/// <see cref="Custom"/> is the model-agnostic path: instead of one of the three hardcoded
/// architectures above, run whichever model <c>Detection.LocalModelName</c> names from the models
/// directory (<c>C:\ProgramData\LarisVMS\models</c>), decoded via whatever
/// <c>LarisVMS.Vision.Models.ModelDiscovery</c> resolved for it (embedded ONNX metadata, or a
/// same-basename JSON sidecar) rather than a hand-written engine class — see
/// <c>LarisVMS.Vision.Inference.GenericOnnxEngine</c>.
/// </summary>
public enum DetectionModelFamily
{
    /// <summary>Resolves to YoloX regardless of accelerator (see DetectionModelSelection.Choose).</summary>
    Auto = 0,
    DFine = 1,
    RfDetr = 2,
    YoloX = 3,
    Custom = 4,
}
