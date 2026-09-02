namespace LarisVMS.Core.Enums;

/// <summary>Which YOLOX model size a node loads — the per-family model-variant selector, the same
/// role <see cref="DFineWeights"/> plays for D-FINE. Node-scoped (Setting + SettingOverride(Scope.Node)),
/// not per-camera: one Vision Service process serves every camera on a node from one loaded model, so
/// an admin picks a size to match that node's hardware — Nano/Tiny on a low-power box, L/X on a
/// strong GPU. A string wire value, no schema.
///
/// darknet53 is deliberately absent: Megvii publishes it only as a .pth needing a torch export step,
/// while nano…x all have ready ONNX. It can be added later as another enum value + fetch entry.</summary>
public enum YoloXSize
{
    Nano = 0,
    Tiny = 1,
    S = 2,
    M = 3,
    L = 4,
    X = 5,
}
