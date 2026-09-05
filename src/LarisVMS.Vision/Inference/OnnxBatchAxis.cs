using Google.Protobuf;
using Onnx;

namespace LarisVMS.Vision.Inference;

/// <summary>
/// Rewrites a YOLOX ONNX graph (Megvii's own pinned export — see <c>tools/export-models/fetch_yolox.py</c>)
/// to accept a dynamic batch dimension, in memory at load time, using the same
/// <c>Google.Protobuf</c> + vendored <c>onnx-ml.proto</c> machinery <see cref="OnnxPreprocessHead"/>
/// already uses for its own graph surgery.
///
/// <b>Why this is needed at all</b>: <c>fetch_yolox.py</c> downloads Megvii's published export with
/// a hardcoded batch-1 input (<c>images [1,3,640,640]</c>) and three per-head <c>Reshape</c> nodes
/// whose target-shape initializer is the literal <c>[1,85,-1]</c> — ONNX's <c>Reshape</c> treats a
/// literal 1 there as "the output's own first dim really is 1", not "copy the input's batch dim".
///
/// <b>Verified directly against the real pinned <c>yolox_s.onnx</c> export (2026-09-03)</b>:
/// rewriting those three initializers to <c>[0,85,-1]</c> (ONNX's documented "0 = copy this dim from
/// the input" <c>Reshape</c> special case), setting a symbolic batch <c>dim_param</c> on the graph's
/// own input/output, and clearing the graph's <c>value_info</c> (an intermediate tensor's stale
/// exported shape hint otherwise makes ONNX Runtime log a spurious "VerifyOutputSizes" mismatch
/// warning on every single inference — confirmed by first reproducing it, then confirming it's gone
/// once <c>value_info</c> is cleared) together make the graph batch-dynamic. A batch-2 run's output
/// is bit-identical to two separate batch-1 runs (max abs diff <c>0.0</c>) — this is a pure
/// shape-metadata edit, not a numeric change.
///
/// D-FINE's own export is already batch-dynamic (<c>pixel_values [batch_size,3,H,W]</c>) — this
/// class is YOLOX-only, and <see cref="MakeBatchDynamic"/> is a no-op passthrough at
/// <c>batchSize</c> 1, so a plain single-image pipeline never parses or re-serializes the model at
/// all and its behavior is completely unchanged from before this class existed.
///
/// <b>Chosen over re-exporting the pinned model in <c>fetch_yolox.py</c></b> so a server's
/// already-cached <c>yolox_*.onnx</c> files keep working with no operator re-fetch, and
/// <c>tools/export-models/</c> gains no <c>onnx</c>/<c>torch</c> dependency for what
/// <see cref="OnnxPreprocessHead"/> already proves is doable in pure C#.
/// </summary>
public static class OnnxBatchAxis
{
    /// <summary>Returns <paramref name="model"/> byte-for-byte unchanged when
    /// <paramref name="batchSize"/> is 1 — the common case. Otherwise parses it, rewrites every
    /// Reshape-target initializer matching the fixed-batch-1 shape <c>[1, 5 + numClasses, -1]</c> to
    /// its batch-dynamic form, sets a symbolic batch <c>dim_param</c> on every graph input/output,
    /// and clears the graph's <c>value_info</c> so ONNX Runtime re-infers shapes silently instead of
    /// trusting a stale batch-1 hint recorded at export time (see this class's own doc comment).
    /// Throws if no matching initializer is found at all — a change to the pinned export's own graph
    /// shape that needs this rewrite re-verified before <paramref name="batchSize"/> &gt; 1 is safe
    /// to ship, not a silent no-op that would leave the graph rejecting a real batch at Run time.</summary>
    public static byte[] MakeBatchDynamic(byte[] model, int batchSize, int numClasses)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (batchSize <= 1) return model;
        if (numClasses <= 0) throw new ArgumentOutOfRangeException(nameof(numClasses), numClasses, "Must be positive.");

        var proto = ModelProto.Parser.ParseFrom(model);
        var graph = proto.Graph;

        var rewritten = 0;
        foreach (var init in graph.Initializer)
        {
            if (!TryReadFixedBatchOneShape(init, numClasses, out var usesRawData)) continue;

            // 0 = copy the input's own batch dim (ONNX Reshape semantics).
            long[] newValues = [0, 5 + numClasses, -1];
            if (usesRawData)
            {
                // The real pinned export stores these three values packed as raw_data (24
                // little-endian bytes), not int64_data — confirmed directly against yolox_s.onnx,
                // where every matching initializer had int64_data empty and raw_data.Length == 24.
                // Write back in the same form: an ONNX reader that trusts raw_data over int64_data
                // when both could theoretically be present would otherwise still see the old value.
                var bytes = new byte[24];
                for (var k = 0; k < 3; k++) BitConverter.GetBytes(newValues[k]).CopyTo(bytes, k * 8);
                init.RawData = ByteString.CopyFrom(bytes);
            }
            else
            {
                init.Int64Data.Clear();
                init.Int64Data.AddRange(newValues);
            }
            rewritten++;
        }
        if (rewritten == 0)
        {
            throw new InvalidOperationException(
                $"OnnxBatchAxis found no fixed-batch-1 Reshape target initializers matching " +
                $"[1,{5 + numClasses},-1] in this model — the pinned YOLOX export's graph shape may " +
                "have changed since this rewrite was verified against it. Re-verify before shipping " +
                "BatchSize > 1 against a re-pinned export.");
        }

        // A literal batch-1 anywhere on the graph's own declared input/output would contradict the
        // Reshape rewrite above and make ONNX Runtime reject the graph as internally inconsistent.
        foreach (var valueInfo in graph.Input.Concat(graph.Output))
            SetSymbolicBatchDim(valueInfo);

        // Empirically necessary, not defensive: an intermediate tensor's value_info entry (recorded
        // by the export's own shape inference pass, not necessarily the final output's) otherwise
        // still declares a literal batch-1 shape, and ONNX Runtime's VerifyOutputSizes check trusts
        // that recorded hint over re-inferring from the (now batch-dynamic) Reshape targets above —
        // logging a mismatch warning on every single inference. Clearing all of it is safe: value_info
        // is purely an optional shape-inference hint, never required for a graph to load or run.
        graph.ValueInfo.Clear();

        return proto.ToByteArray();
    }

    /// <summary>Checks for the exact int64 values <c>fetch_yolox.py</c>'s pinned export bakes into
    /// its three per-head Reshape target initializers — <c>[1, 5+numClasses, -1]</c> — and reports
    /// which of ONNX's two equally-legal storage forms this tensor actually used, so the caller can
    /// write back in the same form. Matched by value rather than by initializer name — the export
    /// doesn't name them predictably, and a value match is exactly as safe here: nothing else in this
    /// graph reshapes to a fixed batch of 1 with a class-count-derived middle dimension.
    ///
    /// <b>Reads <see cref="TensorProto.RawData"/> as well as <see cref="TensorProto.Int64Data"/></b>:
    /// the real pinned export stores these three values as 24 packed little-endian bytes in
    /// <c>raw_data</c>, not in <c>int64_data</c> — checking <c>int64_data</c> alone (the first version
    /// of this method did) silently matched nothing at all against the real model, an error the
    /// Python <c>onnx</c> library's own <c>numpy_helper.to_array</c> hides by handling both forms
    /// transparently. A from-scratch protobuf implementation gets no such help for free.</summary>
    private static bool TryReadFixedBatchOneShape(TensorProto tensor, int numClasses, out bool usesRawData)
    {
        usesRawData = false;
        if (tensor.DataType != (int)TensorProto.Types.DataType.Int64 || tensor.Dims.Count != 1 || tensor.Dims[0] != 3)
            return false;

        long v0, v1, v2;
        if (tensor.Int64Data.Count == 3)
        {
            (v0, v1, v2) = (tensor.Int64Data[0], tensor.Int64Data[1], tensor.Int64Data[2]);
        }
        else if (!tensor.RawData.IsEmpty && tensor.RawData.Length == 24)
        {
            var bytes = tensor.RawData.ToByteArray();
            v0 = BitConverter.ToInt64(bytes, 0);
            v1 = BitConverter.ToInt64(bytes, 8);
            v2 = BitConverter.ToInt64(bytes, 16);
            usesRawData = true;
        }
        else
        {
            return false;
        }

        return v0 == 1 && v1 == 5 + numClasses && v2 == -1;
    }

    private static void SetSymbolicBatchDim(ValueInfoProto valueInfo)
    {
        var shape = valueInfo.Type?.TensorType?.Shape;
        if (shape is null || shape.Dim.Count == 0) return;
        shape.Dim[0] = new TensorShapeProto.Types.Dimension { DimParam = "batch" };
    }
}
