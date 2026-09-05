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

        // Give this head's own value names a size-dependent tag before splicing — see
        // RetagGeneratedNames' own doc comment for why an identical name set across two differently
        // shaped heads is a correctness bug under TensorRT, not just a cosmetic one. Two D-FINE
        // models at different network sizes (416 vs 640) are the case this protects here.
        RetagGeneratedNames(nodes, inits, "head_", $"head{w}x{h}_");

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

    /// <summary>
    /// Rewrites every generated value name in <paramref name="nodes"/>/<paramref name="inits"/> that
    /// starts with <paramref name="oldPrefix"/> to start with <paramref name="newPrefix"/> instead,
    /// then recomputes each node's own name from its (now retagged) first output, so the whole head
    /// is uniquely named for the exact shape it was built for.
    ///
    /// <b>This is a correctness fix, not cosmetics.</b> ONNX Runtime's TensorRT execution provider
    /// keys its compiled-engine cache on a hash (<c>TRTGenerateId</c>) built from the model's file
    /// name plus the *names* of the graph inputs and of every node's outputs — never from shapes,
    /// and never from initializer values. Both heads here are merged in memory and the session is
    /// constructed from bytes, so there is no file name to contribute anything either. Everything
    /// that actually varies between two cameras — the <c>nv12</c> input's declared shape, and (for
    /// <see cref="MergeSliced"/>) the per-tile Slice start/end initializers — is therefore invisible
    /// to that hash.
    ///
    /// With one fixed name set, two Slice cameras of different resolutions but the same slice count
    /// produced the same hash and collided on one cached <c>.engine</c> file. The second camera
    /// deserialized the first camera's engine in under a second and then failed every single frame
    /// with <c>TensorRT EP failed to call setInputShape() for input 'nv12'</c> — the engine was built
    /// static for the first camera's capture size. Tagging the names makes each shape variant a
    /// distinct graph identity, so each gets its own engine.
    ///
    /// The graph input <c>nv12</c> and the model's own input name are deliberately left alone: they
    /// are part of the contract with the engines that bind and read them
    /// (<c>DFineEngine.DetectSliced</c> names <c>nv12</c> literally), and retagging the node outputs
    /// alone already changes the hash.
    /// </summary>
    private static void RetagGeneratedNames(List<NodeProto> nodes, List<TensorProto> inits, string oldPrefix, string newPrefix)
    {
        string Rename(string name) =>
            name.StartsWith(oldPrefix, StringComparison.Ordinal) ? newPrefix + name[oldPrefix.Length..] : name;

        foreach (var init in inits) init.Name = Rename(init.Name);

        foreach (var node in nodes)
        {
            for (var i = 0; i < node.Input.Count; i++) node.Input[i] = Rename(node.Input[i]);
            for (var i = 0; i < node.Output.Count; i++) node.Output[i] = Rename(node.Output[i]);
            // Same construction N() uses, re-derived from the retagged output so node names stay
            // unique across variants too (ORT hashes output names, but a duplicate node name in one
            // graph is its own problem and this keeps both properties in one place).
            node.Name = $"head_{node.OpType}_{node.Output[0]}";
        }
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

    /// <summary>
    /// Detection/hardware-acceleration overhaul, pass 4 (frame slicing): merges an nv12 -&gt; N-way
    /// sliced, normalized-RGB-tensor preprocessing head into a detection model's ONNX graph, in
    /// memory, at load time. Same accelerator-side approach as <see cref="Merge"/> (standard ONNX
    /// ops only, runs on whatever execution provider the model itself does) but for
    /// <see cref="LarisVMS.Core.Enums.AspectMode.Slice"/>'s non-square capture buffer: converts the
    /// whole <paramref name="layout"/>'s CaptureWidth×CaptureHeight frame to normalized planar RGB
    /// once, then cuts it into <paramref name="layout"/>'s slices and concatenates them into one
    /// <c>[N,3,NetworkSize,NetworkSize]</c> batch. Convert-then-slice, not slice-then-convert:
    /// slices overlap, so converting first touches the frame's pixels once rather than reconverting
    /// the overlapped region for every slice that includes it.
    ///
    /// A fully independent method from <see cref="Merge"/> rather than a shared refactor, even
    /// though the nv12→YUV→RGB colour math is identical — <see cref="Merge"/> is an already-shipped,
    /// tested feature (Detection.GpuPreprocessing) and this project's own convention throughout is
    /// to duplicate a small amount of logic rather than risk a working path for an unrelated new one
    /// (see e.g. VisionSession's own class doc comment for the same trade-off elsewhere).
    ///
    /// <paramref name="rescaleTo01"/>/<paramref name="rgbChannelOrder"/> exist because D-FINE and
    /// YOLOX disagree on both: D-FINE wants RGB in [0,1] (rescale, no further normalize — see
    /// <see cref="DFineEngine"/>'s own doc comment); YOLOX wants BGR in [0,255] (Megvii's own
    /// preprocessing convention — see <see cref="YoloXEngine"/>'s own doc comment). Getting either
    /// wrong produces plausible-looking but numerically wrong detections, not an obvious failure —
    /// the same trap <see cref="Merge"/>'s own doc comment already calls out.
    ///
    /// <b>Ordering with <see cref="OnnxBatchAxis"/> for YOLOX</b>: this method only ever touches
    /// preprocessing nodes — it has no idea the model's own internal per-head <c>Reshape</c> targets
    /// are hardcoded to batch 1 (see <see cref="OnnxBatchAxis"/>'s own doc comment). For YOLOX,
    /// <see cref="OnnxBatchAxis.MakeBatchDynamic"/> must run first, on the model's raw bytes; this
    /// method then merges the slicing head into *that* already-batch-dynamic result. D-FINE's own
    /// export is already batch-dynamic and needs no such pre-step.
    /// </summary>
    public static byte[] MergeSliced(byte[] model, SliceLayout layout, string modelInputName, bool rescaleTo01, bool rgbChannelOrder)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(layout);
        if (string.IsNullOrWhiteSpace(modelInputName))
            throw new ArgumentException("Model input name must be provided.", nameof(modelInputName));

        var proto = ModelProto.Parser.ParseFrom(model);
        var graph = proto.Graph;

        // Lower bound than Merge's own opset >= 13 gate — deliberately: the real pinned YOLOX export
        // is opset 11 (confirmed directly), where Slice/Clip already take their extra parameters as
        // inputs (changed at opset 10/11 respectively) but Unsqueeze still takes axes as an
        // *attribute*, not an input (that only changed at opset 13). Both forms are built below,
        // branched on this exact version, rather than gating YOLOX out of slicing entirely.
        var defaultOpset = proto.OpsetImport.FirstOrDefault(o => string.IsNullOrEmpty(o.Domain));
        if (defaultOpset is null || defaultOpset.Version < 11)
        {
            throw new NotSupportedException(
                $"OnnxPreprocessHead needs the default ONNX opset >= 11 (Slice/Clip-as-input semantics); " +
                $"model has {(defaultOpset?.Version.ToString() ?? "none")}. Re-export the model at a newer opset.");
        }
        var unsqueezeAxesAsInput = defaultOpset.Version >= 13;

        if (graph.Input.All(v => v.Name != modelInputName))
            throw new InvalidOperationException($"Model has no '{modelInputName}' graph input to replace.");

        int capW = layout.CaptureWidth, capH = layout.CaptureHeight;
        int uvW = capW / 2, uvH = capH / 2;
        int nv12Rows = capH + uvH;
        int netSize = layout.NetworkSize;

        var nodes = new List<NodeProto>();
        var inits = new List<TensorProto>();

        // ── constants — same shape math as Merge, parameterized on the whole capture frame ──
        inits.Add(I64("hs_y_starts", [0, 0, 0]));
        inits.Add(I64("hs_y_ends", [1, capH, capW]));
        inits.Add(I64("hs_axes012", [0, 1, 2]));
        inits.Add(I64("hs_axes0123", [0, 1, 2, 3]));
        inits.Add(I64("hs_uv_starts", [0, capH, 0]));
        inits.Add(I64("hs_uv_ends", [1, nv12Rows, capW]));
        inits.Add(I64("hs_uv_shape", [1, uvH, uvW, 2]));
        inits.Add(I64("hs_axis3", [3]));
        inits.Add(I64("hs_u_starts", [0]));
        inits.Add(I64("hs_u_ends", [1]));
        inits.Add(I64("hs_v_starts", [1]));
        inits.Add(I64("hs_v_ends", [2]));
        inits.Add(I64("hs_chroma_flat", [1, uvH, uvW]));
        inits.Add(I64("hs_chroma_split", [1, uvH, 1, uvW, 1]));
        inits.Add(I64("hs_chroma_expand", [1, uvH, 2, uvW, 2]));
        inits.Add(I64("hs_chroma_full", [1, capH, capW]));
        inits.Add(I64("hs_axis1", [1]));

        inits.Add(F32("hs_c16", 16f));
        inits.Add(F32("hs_c128", 128f));
        inits.Add(F32("hs_kc", Bt601Limited.Kc));
        inits.Add(F32("hs_kr", Bt601Limited.Kr));
        inits.Add(F32("hs_kgu", Bt601Limited.Kgu));
        inits.Add(F32("hs_kgv", Bt601Limited.Kgv));
        inits.Add(F32("hs_kb", Bt601Limited.Kb));
        inits.Add(F32("hs_zero", 0f));
        inits.Add(F32("hs_c255", 255f));
        if (rescaleTo01) inits.Add(F32("hs_inv255", 1f / 255f));

        // ── nv12 (uint8) → float ──
        nodes.Add(N("Cast", ["nv12"], ["hs_nv12f"], A("to", (long)TensorProto.Types.DataType.Float)));

        // ── Y plane [1,capH,capW] ──
        nodes.Add(N("Slice", ["hs_nv12f", "hs_y_starts", "hs_y_ends", "hs_axes012"], ["hs_Y"]));

        // ── UV rows → U/V [1,capH,capW] each (nearest 2x chroma upsample) ──
        nodes.Add(N("Slice", ["hs_nv12f", "hs_uv_starts", "hs_uv_ends", "hs_axes012"], ["hs_UVrows"]));
        nodes.Add(N("Reshape", ["hs_UVrows", "hs_uv_shape"], ["hs_UV"]));
        nodes.Add(N("Slice", ["hs_UV", "hs_u_starts", "hs_u_ends", "hs_axis3"], ["hs_Usel"]));
        nodes.Add(N("Slice", ["hs_UV", "hs_v_starts", "hs_v_ends", "hs_axis3"], ["hs_Vsel"]));
        nodes.Add(N("Reshape", ["hs_Usel", "hs_chroma_flat"], ["hs_Uh"]));
        nodes.Add(N("Reshape", ["hs_Vsel", "hs_chroma_flat"], ["hs_Vh"]));
        UpsampleChromaSliced("hs_Uh", "hs_U");
        UpsampleChromaSliced("hs_Vh", "hs_V");

        // ── BT.601 limited-range YUV → RGB (0-255) ──
        nodes.Add(N("Sub", ["hs_Y", "hs_c16"], ["hs_C"]));
        nodes.Add(N("Sub", ["hs_U", "hs_c128"], ["hs_D"]));
        nodes.Add(N("Sub", ["hs_V", "hs_c128"], ["hs_E"]));
        nodes.Add(N("Mul", ["hs_C", "hs_kc"], ["hs_Cs"]));

        nodes.Add(N("Mul", ["hs_E", "hs_kr"], ["hs_rE"]));
        nodes.Add(N("Add", ["hs_Cs", "hs_rE"], ["hs_R0"]));

        nodes.Add(N("Mul", ["hs_D", "hs_kgu"], ["hs_gD"]));
        nodes.Add(N("Mul", ["hs_E", "hs_kgv"], ["hs_gE"]));
        nodes.Add(N("Sub", ["hs_Cs", "hs_gD"], ["hs_G1"]));
        nodes.Add(N("Sub", ["hs_G1", "hs_gE"], ["hs_G0"]));

        nodes.Add(N("Mul", ["hs_D", "hs_kb"], ["hs_bD"]));
        nodes.Add(N("Add", ["hs_Cs", "hs_bD"], ["hs_B0"]));

        // ── clip [0,255], optionally /255, stack to [1,3,capH,capW] ──
        NormalizeChannelSliced("hs_R0", "hs_Rn");
        NormalizeChannelSliced("hs_G0", "hs_Gn");
        NormalizeChannelSliced("hs_B0", "hs_Bn");
        Unsq("hs_Rn", "hs_Ru");
        Unsq("hs_Gn", "hs_Gu");
        Unsq("hs_Bn", "hs_Bu");

        var channelOrder = rgbChannelOrder ? new[] { "hs_Ru", "hs_Gu", "hs_Bu" } : new[] { "hs_Bu", "hs_Gu", "hs_Ru" };
        nodes.Add(N("Concat", channelOrder, ["hs_full"], A("axis", 1L)));

        // ── N × Slice + Concat → [N,3,netSize,netSize] ──
        var sliceOutputs = new string[layout.Slices.Count];
        for (var i = 0; i < layout.Slices.Count; i++)
        {
            var tile = layout.Slices[i];
            inits.Add(I64($"hs_tile{i}_starts", [0, 0, tile.Y, tile.X]));
            inits.Add(I64($"hs_tile{i}_ends", [1, 3, tile.Y + netSize, tile.X + netSize]));
            sliceOutputs[i] = $"hs_tile{i}";
            nodes.Add(N("Slice", ["hs_full", $"hs_tile{i}_starts", $"hs_tile{i}_ends", "hs_axes0123"], [sliceOutputs[i]]));
        }
        nodes.Add(N("Concat", sliceOutputs, [modelInputName], A("axis", 0L)));

        // Give this layout's own value names a capture-size + slice-count tag before splicing. This
        // is the fix for the 0.186.0 defect where two Slice cameras of different resolutions shared
        // one TensorRT engine cache entry and the second camera failed every frame — see
        // RetagGeneratedNames' own doc comment for the full mechanism. Do not "simplify" these back
        // to constant names.
        RetagGeneratedNames(nodes, inits, "hs_", $"hs{capW}x{capH}n{layout.Slices.Count}_");

        // ── splice into the graph ──
        var original = graph.Node.ToList();
        graph.Node.Clear();
        graph.Node.AddRange(nodes);
        graph.Node.AddRange(original);
        graph.Initializer.AddRange(inits);

        var oldInput = graph.Input.First(v => v.Name == modelInputName);
        graph.Input.Remove(oldInput);
        graph.Input.Add(new ValueInfoProto
        {
            Name = "nv12",
            Type = new TypeProto
            {
                TensorType = new TypeProto.Types.Tensor
                {
                    ElemType = (int)TensorProto.Types.DataType.Uint8,
                    Shape = new TensorShapeProto { Dim = { Dim(1), Dim(nv12Rows), Dim(capW) } }
                }
            }
        });

        // The model's own declared output batch dim is now N, not 1 — force it symbolic so a stale
        // recorded shape can't make ONNX Runtime log a spurious VerifyOutputSizes warning on every
        // inference (see OnnxBatchAxis's own doc comment for the same finding on the YOLOX side).
        foreach (var output in graph.Output)
        {
            if (output.Type?.TensorType?.Shape is { Dim.Count: > 0 } shape)
                shape.Dim[0] = new TensorShapeProto.Types.Dimension { DimParam = "batch" };
        }
        graph.ValueInfo.Clear();

        return proto.ToByteArray();

        void UpsampleChromaSliced(string src, string dst)
        {
            nodes.Add(N("Reshape", [src, "hs_chroma_split"], [dst + "_s"]));
            nodes.Add(N("Expand", [dst + "_s", "hs_chroma_expand"], [dst + "_e"]));
            nodes.Add(N("Reshape", [dst + "_e", "hs_chroma_full"], [dst]));
        }

        void NormalizeChannelSliced(string src, string dst)
        {
            nodes.Add(N("Clip", [src, "hs_zero", "hs_c255"], [dst + "_c"]));
            if (rescaleTo01) nodes.Add(N("Mul", [dst + "_c", "hs_inv255"], [dst]));
            else nodes.Add(N("Identity", [dst + "_c"], [dst])); // keep 0-255 range (YOLOX)
        }

        // Unsqueeze's own axes parameter moved from an attribute to an input at opset 13 — see this
        // method's own opset-gate comment. Building the wrong form for the graph's actual opset
        // isn't just a style mismatch: ONNX Runtime's opset-13 Unsqueeze schema has no axes attribute
        // to fall back on, and the opset-11 schema has no axes input, so only one form is valid for
        // any given model.
        void Unsq(string src, string dst)
        {
            nodes.Add(unsqueezeAxesAsInput
                ? N("Unsqueeze", [src, "hs_axis1"], [dst])
                : N("Unsqueeze", [src], [dst], AInts("axes", [1])));
        }
    }

    private static AttributeProto AInts(string name, long[] values)
    {
        var attr = new AttributeProto { Name = name, Type = AttributeProto.Types.AttributeType.Ints };
        attr.Ints.AddRange(values);
        return attr;
    }
}
