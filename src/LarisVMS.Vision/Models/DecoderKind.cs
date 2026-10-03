namespace LarisVMS.Vision.Models;

/// <summary>Which decoder turns a model's raw ONNX output into detections. Ported from SideGlance's
/// <c>Config.DecoderKind</c> (now legal to share — both repos are Apache-2.0) and extended with
/// <see cref="DFine"/> so a descriptor-driven model can select D-FINE's existing decode math through
/// the same mechanism as the YOLO-family shapes, instead of a second bespoke dispatch path.</summary>
public enum DecoderKind
{
    /// <summary>Ultralytics v8 / v11 detect head: <c>[B, 4+nc, N]</c> (or its transpose
    /// <c>[B, N, 4+nc]</c>), class scores already sigmoid'd, no objectness. Needs class-aware NMS.</summary>
    Ultralytics,

    /// <summary>End-to-end / NMS-free head: <c>[B, N, 6]</c> = x1,y1,x2,y2,score,cls. YOLOv10,
    /// YOLO26, and v8/v11 exported with <c>nms=True</c>. Score filter only.</summary>
    EndToEnd,

    /// <summary>D-FINE / DETR-style head: two named outputs, <c>logits [B,Q,C]</c> and
    /// <c>pred_boxes [B,Q,4]</c>. One query per object — no NMS needed. Wraps the existing
    /// <see cref="LarisVMS.Vision.Inference.DFineDecoder"/> math.</summary>
    DFine,

    /// <summary>Pick <see cref="Ultralytics"/>, <see cref="EndToEnd"/>, or <see cref="DFine"/> from
    /// the model's own output names/shape at load — see <see cref="LarisVMS.Vision.Inference.Decoders.DecoderFactory"/>.</summary>
    Auto,
}
