using LarisVMS.Vision.Models;

namespace LarisVMS.Vision.Inference.Decoders;

/// <summary>Chooses the concrete decoder for a descriptor-driven model. <see cref="DecoderKind.Auto"/>
/// first checks for D-FINE's two-output signature (<c>logits</c> + <c>pred_boxes</c>) ahead of the
/// YOLO-family shape checks — this is what lets D-FINE-style and YOLO-style models share one dispatch
/// mechanism (see <see cref="ModelDiscovery"/>'s own doc comment). Otherwise it inspects the single
/// output's rank/shape against the label count: <c>[B, 4+nc, N]</c> or <c>[B, N, 4+nc]</c> →
/// Ultralytics; <c>[B, N, 6]</c> → end-to-end. An explicit decoder kind is validated against the shape
/// too — the alternative is a decoder that resolves fine at load time and then throws on every live
/// request instead of failing model discovery with a clear reason.
///
/// Ported from SideGlance's own <c>Inference.Decoders.DecoderFactory</c> and extended with the D-FINE
/// case.</summary>
public static class DecoderFactory
{
    public static IDetectionDecoder Resolve(DecoderKind kind, OnnxModelInfo info, int labelCount) => kind switch
    {
        DecoderKind.DFine => RequireDFineShape(info),
        DecoderKind.Ultralytics => RequireUltralyticsShape(SingleOutputDims(info), labelCount),
        DecoderKind.EndToEnd => RequireEndToEndShape(SingleOutputDims(info)),
        DecoderKind.Auto => Detect(info, labelCount),
        _ => throw new DecoderShapeException($"unknown decoder kind {kind}."),
    };

    private static IDetectionDecoder Detect(OnnxModelInfo info, int labelCount)
    {
        if (IsDFineShape(info)) return new DFineDecoderAdapter();

        var dims = SingleOutputDims(info);
        if (dims.Count != 3)
            throw new DecoderShapeException(
                $"cannot auto-detect a decoder for a rank-{dims.Count} output [{string.Join(",", dims)}] — " +
                "supply a sidecar JSON with 'decoder' set explicitly.");

        var attrs = 4 + labelCount;
        if (dims[1] == attrs || dims[2] == attrs) return new UltralyticsDecoder();
        if (dims[2] == 6) return new EndToEndDecoder();

        throw new DecoderShapeException(
            $"output [{string.Join(",", dims)}] matches neither an Ultralytics head (a dim == {attrs}) nor an " +
            "end-to-end head (last dim == 6) nor D-FINE's two-output signature — supply a sidecar JSON with " +
            "'decoder' set explicitly.");
    }

    private static bool IsDFineShape(OnnxModelInfo info) =>
        info.OutputNames.Contains("logits", StringComparer.Ordinal)
        && info.OutputNames.Contains("pred_boxes", StringComparer.Ordinal);

    private static IDetectionDecoder RequireDFineShape(OnnxModelInfo info)
    {
        if (!IsDFineShape(info))
            throw new DecoderShapeException(
                "'decoder' is \"dfine\" but the model's outputs are " +
                $"[{string.Join(",", info.OutputNames)}], not the expected 'logits'/'pred_boxes' pair.");
        return new DFineDecoderAdapter();
    }

    private static IReadOnlyList<int?> SingleOutputDims(OnnxModelInfo info)
    {
        if (info.Outputs.Count != 1)
            throw new DecoderShapeException(
                $"this decoder expects exactly one graph output, but the model declares {info.Outputs.Count} " +
                $"([{string.Join(",", info.OutputNames)}]).");
        return info.Outputs[0].Dims;
    }

    private static IDetectionDecoder RequireUltralyticsShape(IReadOnlyList<int?> dims, int labelCount)
    {
        var attrs = 4 + labelCount;
        if (dims.Count != 3 || (dims[1] != attrs && dims[2] != attrs))
            throw new DecoderShapeException(
                $"'decoder' is \"ultralytics\" but the model's output is [{string.Join(",", dims)}] — neither dim " +
                $"is 4+labelCount ({attrs}). This looks like an end-to-end/NMS-baked export; set 'decoder' to " +
                "\"endToEnd\" or \"auto\".");
        return new UltralyticsDecoder();
    }

    private static IDetectionDecoder RequireEndToEndShape(IReadOnlyList<int?> dims)
    {
        if (dims.Count != 3 || dims[2] != 6)
            throw new DecoderShapeException(
                $"'decoder' is \"endToEnd\" but the model's output is [{string.Join(",", dims)}], not [B, N, 6]. " +
                "This looks like a plain (non-NMS) Ultralytics export; set 'decoder' to \"ultralytics\" or \"auto\".");
        return new EndToEndDecoder();
    }
}
