using Google.Protobuf;
using Onnx;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using LarisVMS.Vision.Inference;

namespace LarisVMS.Tests;

/// <summary>
/// Verifies OnnxBatchAxis.MakeBatchDynamic's graph rewrite on a synthetic model shaped like YOLOX's
/// own fixed-batch-1 export (see that class's own doc comment for the real-model finding this
/// codifies), plus an end-to-end run against the real pinned yolox_s.onnx where one happens to be
/// checked out locally — same gitignored-model-guard pattern as OnnxPreprocessHeadTests' own
/// real-D-FINE-model case.
/// </summary>
public class OnnxBatchAxisTests
{
    private const int NumClasses = 80; // YOLOX's own COCO-80 vocabulary

    [Fact]
    public void BatchSizeOne_ReturnsTheExactSameBytesUnchanged()
    {
        var model = BuildSyntheticModel();
        var result = OnnxBatchAxis.MakeBatchDynamic(model, batchSize: 1, NumClasses);

        // Reference equality, not just byte equality: batchSize 1 must be a true no-op passthrough —
        // a plain single-image pipeline never even parses/re-serializes the model.
        Assert.Same(model, result);
    }

    [Fact]
    public void BatchSizeTwo_RewritesTheFixedBatchReshapeTargetsAndSymbolicBatchDims()
    {
        var model = BuildSyntheticModel();
        var rewritten = OnnxBatchAxis.MakeBatchDynamic(model, batchSize: 2, NumClasses);

        var proto = ModelProto.Parser.ParseFrom(rewritten);
        var target = proto.Graph.Initializer.Single(i => i.Name == "reshape_target");
        Assert.Equal([0L, 5 + NumClasses, -1L], target.Int64Data);

        var input = proto.Graph.Input.Single(v => v.Name == "images");
        var inputDim0 = input.Type.TensorType.Shape.Dim[0];
        Assert.Equal(TensorShapeProto.Types.Dimension.ValueOneofCase.DimParam, inputDim0.ValueCase);
        Assert.Equal("batch", inputDim0.DimParam);

        var output = proto.Graph.Output.Single(v => v.Name == "output");
        var outputDim0 = output.Type.TensorType.Shape.Dim[0];
        Assert.Equal(TensorShapeProto.Types.Dimension.ValueOneofCase.DimParam, outputDim0.ValueCase);
        Assert.Equal("batch", outputDim0.DimParam);

        // The stale batch-1 hint that reproduced ONNX Runtime's real "VerifyOutputSizes" warning
        // (see this class's own doc comment) must be gone entirely.
        Assert.Empty(proto.Graph.ValueInfo);
    }

    [Fact]
    public void NoMatchingReshapeTarget_ThrowsRatherThanSilentlyDoingNothing()
    {
        var model = BuildSyntheticModel(includeReshapeTarget: false);

        // A silent no-op here would ship a graph that still rejects a real batch at Run time — this
        // must fail loudly instead, at load time, where it's diagnosable.
        Assert.Throws<InvalidOperationException>(() => OnnxBatchAxis.MakeBatchDynamic(model, batchSize: 2, NumClasses));
    }

    [Fact]
    public void RewrittenGraph_LoadsAndRunsAgainstTheRealPinnedYoloXModel()
    {
        // yolox_s.onnx is gitignored (tools/export-models/fetch_yolox.py fetches it), so this only
        // runs where one has actually been fetched onto this machine — the end-to-end check that the
        // rewrite produces a graph ONNX Runtime accepts and that a real batch>1 Run succeeds, not just
        // that the protobuf edit looks right in isolation.
        var modelPath = FindRepoFile(Path.Combine("models", "yolox_s.onnx"));
        if (modelPath is null) return;

        var rewritten = OnnxBatchAxis.MakeBatchDynamic(File.ReadAllBytes(modelPath), batchSize: 2, NumClasses);

        using var session = new InferenceSession(rewritten);
        var inputName = session.InputMetadata.Keys.First();

        var batch = new DenseTensor<float>([2, 3, 640, 640]);
        // Deterministic non-zero fill — an all-zero input is a degenerate case some graphs special-case.
        for (var i = 0; i < batch.Length; i++) batch.Buffer.Span[i] = (i * 37 + 11) % 255;

        using var results = session.Run([NamedOnnxValue.CreateFromTensor(inputName, batch)]);
        var output = results.First().AsTensor<float>();
        Assert.Equal(2, output.Dimensions[0]);
        Assert.Equal(85, output.Dimensions[2]); // 5 + NumClasses
    }

    private static string? FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, relative);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>A minimal graph shaped like the one fact this class's rewrite depends on: an
    /// initializer whose int64 values are exactly the pinned export's fixed-batch-1 Reshape target
    /// <c>[1, 5+numClasses, -1]</c>, a batch-1 graph input/output, and one stray value_info entry
    /// standing in for the stale intermediate-shape hint the real export carries (see this class's
    /// own doc comment for why clearing it matters). Not wired into any actual Reshape/compute node —
    /// MakeBatchDynamic only ever inspects initializers/input/output/value_info, never node graph
    /// topology, so nothing here needs to be a runnable graph.</summary>
    private static byte[] BuildSyntheticModel(bool includeReshapeTarget = true)
    {
        var graph = new GraphProto { Name = "yolox-like" };

        if (includeReshapeTarget)
        {
            var target = new TensorProto { Name = "reshape_target", DataType = (int)TensorProto.Types.DataType.Int64 };
            target.Dims.Add(3);
            target.Int64Data.AddRange([1L, 5 + NumClasses, -1L]);
            graph.Initializer.Add(target);
        }

        graph.Input.Add(Vi("images", [1, 3, 640, 640]));
        graph.Output.Add(Vi("output", [1, 8400, 5 + NumClasses]));
        // Stand-in for an intermediate tensor's stale exported shape hint — see this method's own
        // doc comment. Name/shape don't matter; only that MakeBatchDynamic clears every entry.
        graph.ValueInfo.Add(Vi("some_intermediate", [1, 8400, 5 + NumClasses]));

        var model = new ModelProto { IrVersion = 8, ProducerName = "LarisVMS.Tests", Graph = graph };
        model.OpsetImport.Add(new OperatorSetIdProto { Domain = "", Version = 13 });
        return model.ToByteArray();
    }

    private static ValueInfoProto Vi(string name, int[] dims) => new()
    {
        Name = name,
        Type = new TypeProto
        {
            TensorType = new TypeProto.Types.Tensor
            {
                ElemType = (int)TensorProto.Types.DataType.Float,
                Shape = new TensorShapeProto { Dim = { dims.Select(d => new TensorShapeProto.Types.Dimension { DimValue = d }) } }
            }
        }
    };
}
