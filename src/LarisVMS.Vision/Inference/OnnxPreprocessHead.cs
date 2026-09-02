using Google.Protobuf;
using Onnx;

namespace LarisVMS.Vision.Inference;

/// <summary>
/// Pass 4a: merges an nv12 → normalized-RGB-tensor preprocessing head into a D-FINE ONNX graph, in
/// memory, at model-load time. The head is built entirely from standard ONNX ops (Slice / Cast /
/// Reshape / Expand / Sub / Mul / Add / Clip / Unsqueeze / Concat), so ONNX Runtime schedules it on
/// whatever execution provider it schedules the model on — CUDA, DirectML, OpenVINO or CPU — with no
/// vendor-specific code. The per-frame colour conversion and <c>/255</c> normalization that
/// <see cref="DFineEngine"/> used to do in a CPU pixel loop then run on the accelerator instead.
///
/// The original graph input <c>pixel_values</c> (float32 <c>[1,3,H,W]</c>) is replaced by a new input
/// <c>nv12</c> (uint8 <c>[1, H*3/2, W]</c> — the raw <c>scale_cuda</c>+<c>pad</c> output: H rows of
/// Y, then H/2 rows of interleaved UV). The head's final node is named <c>pixel_values</c> so the
/// rest of the model is untouched. Every model output (<c>logits</c>, <c>pred_boxes</c>) is
/// unchanged, so <see cref="DFineDecoder"/> and everything downstream stays exactly as it was.
///
/// Colour matrix: BT.601, limited ("studio") range — the near-universal case for camera sub-streams
/// at =&lt;720p. If a stream declares BT.709 or full range, the six coefficients in
/// <see cref="Bt601Limited"/> are the one thing to change.
/// </summary>
public static class OnnxPreprocessHead
{
    /// <summary>Returns a new serialized ModelProto with the preprocessing head merged in.
    /// <paramref name="networkWidth"/>/<paramref name="networkHeight"/> are the model's fixed input
    /// size (<see cref="InferenceProfile.NetworkWidth"/>/Height) — the head produces exactly that.</summary>
    public static byte[] Merge(byte[] model, int networkWidth, int networkHeight)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (networkWidth <= 0 || networkHeight <= 0 || networkWidth % 2 != 0 || networkHeight % 2 != 0)
            throw new ArgumentException($"Network size must be positive and even, got {networkWidth}x{networkHeight}.");

        var proto = ModelProto.Parser.ParseFrom(model);
        var graph = proto.Graph;

        var defaultOpset = proto.OpsetImport.FirstOrDefault(o => string.IsNullOrEmpty(o.Domain));
        if (defaultOpset is null || defaultOpset.Version < 13)
        {
            throw new NotSupportedException(
                $"OnnxPreprocessHead needs the default ONNX opset >= 13 (Slice/Cast/Unsqueeze-as-input semantics); " +
                $"model has {(defaultOpset?.Version.ToString() ?? "none")}. Re-export the model at a newer opset.");
        }

        const string modelInput = "pixel_values";
        if (graph.Input.All(v => v.Name != modelInput))
            throw new InvalidOperationException($"Model has no '{modelInput}' graph input to replace.");

        int w = networkWidth, h = networkHeight;
        int uvW = w / 2, uvH = h / 2;
        int nv12Rows = h + uvH; // Y rows + interleaved-UV rows

        var nodes = new List<NodeProto>();
        var inits = new List<TensorProto>();

        // ── constants ───────────────────────────────────────────────────────────
        inits.Add(I64("head_y_starts", [0, 0, 0]));
        inits.Add(I64("head_y_ends", [1, h, w]));
        inits.Add(I64("head_axes012", [0, 1, 2]));
        inits.Add(I64("head_uv_starts", [0, h, 0]));
        inits.Add(I64("head_uv_ends", [1, nv12Rows, w]));
        inits.Add(I64("head_uv_shape", [1, uvH, uvW, 2]));
        inits.Add(I64("head_axis3", [3]));
        inits.Add(I64("head_u_starts", [0]));
        inits.Add(I64("head_u_ends", [1]));
        inits.Add(I64("head_v_starts", [1]));
        inits.Add(I64("head_v_ends", [2]));
        inits.Add(I64("head_chroma_flat", [1, uvH, uvW]));
        inits.Add(I64("head_chroma_split", [1, uvH, 1, uvW, 1]));
        inits.Add(I64("head_chroma_expand", [1, uvH, 2, uvW, 2]));
        inits.Add(I64("head_chroma_full", [1, h, w]));
        inits.Add(I64("head_axis1", [1]));

        inits.Add(F32("head_c16", 16f));
        inits.Add(F32("head_c128", 128f));
        inits.Add(F32("head_kc", Bt601Limited.Kc));
        inits.Add(F32("head_kr", Bt601Limited.Kr));
        inits.Add(F32("head_kgu", Bt601Limited.Kgu));
        inits.Add(F32("head_kgv", Bt601Limited.Kgv));
        inits.Add(F32("head_kb", Bt601Limited.Kb));
        inits.Add(F32("head_zero", 0f));
        inits.Add(F32("head_c255", 255f));
        inits.Add(F32("head_inv255", 1f / 255f));

        // ── nv12 (uint8) → float ────────────────────────────────────────────────
        nodes.Add(N("Cast", ["nv12"], ["head_nv12f"], A("to", (long)TensorProto.Types.DataType.Float)));

        // ── Y plane: [1,h,w] ───────────────────────────────────────────────────
        nodes.Add(N("Slice", ["head_nv12f", "head_y_starts", "head_y_ends", "head_axes012"], ["head_Y"]));

        // ── UV rows [1,uvH,w] → [1,uvH,uvW,2] → U/V [1,uvH,uvW] ────────────────
        nodes.Add(N("Slice", ["head_nv12f", "head_uv_starts", "head_uv_ends", "head_axes012"], ["head_UVrows"]));
        nodes.Add(N("Reshape", ["head_UVrows", "head_uv_shape"], ["head_UV"]));
        nodes.Add(N("Slice", ["head_UV", "head_u_starts", "head_u_ends", "head_axis3"], ["head_Usel"]));
        nodes.Add(N("Slice", ["head_UV", "head_v_starts", "head_v_ends", "head_axis3"], ["head_Vsel"]));
        nodes.Add(N("Reshape", ["head_Usel", "head_chroma_flat"], ["head_Uh"]));
        nodes.Add(N("Reshape", ["head_Vsel", "head_chroma_flat"], ["head_Vh"]));

        // ── nearest 2x chroma upsample via Reshape/Expand/Reshape ──────────────
        UpsampleChroma("head_Uh", "head_U");
        UpsampleChroma("head_Vh", "head_V");

        // ── BT.601 limited-range YUV → RGB (0-255) ─────────────────────────────
        nodes.Add(N("Sub", ["head_Y", "head_c16"], ["head_C"]));
        nodes.Add(N("Sub", ["head_U", "head_c128"], ["head_D"]));
        nodes.Add(N("Sub", ["head_V", "head_c128"], ["head_E"]));
        nodes.Add(N("Mul", ["head_C", "head_kc"], ["head_Cs"]));

        nodes.Add(N("Mul", ["head_E", "head_kr"], ["head_rE"]));
        nodes.Add(N("Add", ["head_Cs", "head_rE"], ["head_R0"]));

        nodes.Add(N("Mul", ["head_D", "head_kgu"], ["head_gD"]));
        nodes.Add(N("Mul", ["head_E", "head_kgv"], ["head_gE"]));
        nodes.Add(N("Sub", ["head_Cs", "head_gD"], ["head_G1"]));
        nodes.Add(N("Sub", ["head_G1", "head_gE"], ["head_G0"]));

        nodes.Add(N("Mul", ["head_D", "head_kb"], ["head_bD"]));
        nodes.Add(N("Add", ["head_Cs", "head_bD"], ["head_B0"]));

        // ── clip [0,255], /255, stack to [1,3,h,w] planar RGB ─────────────────
        NormalizeChannel("head_R0", "head_Rn");
        NormalizeChannel("head_G0", "head_Gn");
        NormalizeChannel("head_B0", "head_Bn");
        nodes.Add(N("Unsqueeze", ["head_Rn", "head_axis1"], ["head_Ru"]));
        nodes.Add(N("Unsqueeze", ["head_Gn", "head_axis1"], ["head_Gu"]));
        nodes.Add(N("Unsqueeze", ["head_Bn", "head_axis1"], ["head_Bu"]));
        nodes.Add(N("Concat", ["head_Ru", "head_Gu", "head_Bu"], [modelInput], A("axis", 1L)));

        void UpsampleChroma(string src, string dst)
        {
            nodes.Add(N("Reshape", [src, "head_chroma_split"], [dst + "_s"]));
            nodes.Add(N("Expand", [dst + "_s", "head_chroma_expand"], [dst + "_e"]));
            nodes.Add(N("Reshape", [dst + "_e", "head_chroma_full"], [dst]));
        }

        void NormalizeChannel(string src, string dst)
        {
            nodes.Add(N("Clip", [src, "head_zero", "head_c255"], [dst + "_c"]));
            nodes.Add(N("Mul", [dst + "_c", "head_inv255"], [dst]));
        }

        // ── splice into the graph ─────────────────────────────────────────────
        var original = graph.Node.ToList();
        graph.Node.Clear();
        graph.Node.AddRange(nodes);
        graph.Node.AddRange(original);
        graph.Initializer.AddRange(inits);

        var oldInput = graph.Input.First(v => v.Name == modelInput);
        graph.Input.Remove(oldInput);
        graph.Input.Add(new ValueInfoProto
        {
            Name = "nv12",
            Type = new TypeProto
            {
                TensorType = new TypeProto.Types.Tensor
                {
                    ElemType = (int)TensorProto.Types.DataType.Uint8,
                    Shape = new TensorShapeProto { Dim = { Dim(1), Dim(nv12Rows), Dim(w) } }
                }
            }
        });

        return proto.ToByteArray();
    }

    private static NodeProto N(string op, string[] ins, string[] outs, params AttributeProto[] attrs)
    {
        var node = new NodeProto { OpType = op, Name = $"head_{op}_{outs[0]}" };
        node.Input.AddRange(ins);
        node.Output.AddRange(outs);
        node.Attribute.AddRange(attrs);
        return node;
    }

    private static AttributeProto A(string name, long i) =>
        new() { Name = name, Type = AttributeProto.Types.AttributeType.Int, I = i };

    private static TensorProto I64(string name, long[] values)
    {
        var t = new TensorProto { Name = name, DataType = (int)TensorProto.Types.DataType.Int64 };
        t.Dims.Add(values.Length);
        t.Int64Data.AddRange(values);
        return t;
    }

    private static TensorProto F32(string name, float value)
    {
        var t = new TensorProto { Name = name, DataType = (int)TensorProto.Types.DataType.Float };
        t.FloatData.Add(value); // scalar: no dims
        return t;
    }

    private static TensorShapeProto.Types.Dimension Dim(long v) => new() { DimValue = v };
}
