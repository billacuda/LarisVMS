using Google.Protobuf;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Onnx;
using LarisVMS.Vision.Inference;

namespace LarisVMS.Tests;

/// <summary>
/// Verifies OnnxPreprocessHead.MergeSliced builds a valid graph whose N-way sliced, normalized
/// output matches a straight C# BT.601 conversion of the corresponding region of a synthetic
/// capture frame — the same approach OnnxPreprocessHeadTests uses for the non-sliced head, extended
/// to check that each output batch slot is the *correct sub-region*, not just correctly normalized.
/// Runs entirely on the ONNX Runtime CPU EP against a synthetic identity model, so it needs no GPU
/// and no real model weights.
/// </summary>
public class OnnxPreprocessHeadSlicedTests
{
    // A landscape 16x8 source at network size 8 scales to exactly capture 16x8 (short edge already
    // at the network size) and produces two non-overlapping, side-by-side 8x8 slices spanning the
    // whole width — the simplest case that still exercises "the right pixels went to the right
    // batch slot", with no overlap to complicate hand-checking the reference.
    private const int NetSize = 8;
    private const int CaptureWidth = 16;
    private const int CaptureHeight = 8;

    [Fact]
    public void MergeSliced_DFineStyle_EachBatchSlotIsTheCorrectBt601NormalizedSubRegion()
    {
        var layout = SliceLayout.Create(CaptureWidth, CaptureHeight, NetSize);
        Assert.Equal(2, layout.Slices.Count);
        Assert.Equal(new SliceLayout.Tile(0, 0, NetSize, NetSize), layout.Slices[0]);
        Assert.Equal(new SliceLayout.Tile(8, 0, NetSize, NetSize), layout.Slices[1]);

        var baseModel = BuildIdentityModel(NetSize, NetSize);
        var merged = OnnxPreprocessHead.MergeSliced(baseModel, layout, "pixel_values", rescaleTo01: true, rgbChannelOrder: true);

        var proto = ModelProto.Parser.ParseFrom(merged);
        Assert.Contains(proto.Graph.Input, v => v.Name == "nv12");
        Assert.DoesNotContain(proto.Graph.Input, v => v.Name == "pixel_values");

        var nv12 = SyntheticNv12(CaptureWidth, CaptureHeight);
        var referenceFull = ReferenceConvertRgb01(nv12, CaptureWidth, CaptureHeight);

        using var session = new InferenceSession(merged);
        var input = new DenseTensor<byte>(nv12, [1, CaptureHeight + CaptureHeight / 2, CaptureWidth]);
        using var results = session.Run([NamedOnnxValue.CreateFromTensor("nv12", input)]);
        var output = results.First(o => o.Name == "pixel_values_out").AsTensor<float>();

        Assert.Equal(2, output.Dimensions[0]);
        Assert.Equal(3, output.Dimensions[1]);
        Assert.Equal(NetSize, output.Dimensions[2]);
        Assert.Equal(NetSize, output.Dimensions[3]);

        for (var slice = 0; slice < 2; slice++)
        {
            var tile = layout.Slices[slice];
            for (var c = 0; c < 3; c++)
            for (var y = 0; y < NetSize; y++)
            for (var x = 0; x < NetSize; x++)
            {
                var expected = referenceFull[c, y, tile.X + x];
                var actual = output[slice, c, y, x];
                Assert.True(Math.Abs(expected - actual) < 1e-4f,
                    $"slice {slice} [{c},{y},{x}]: expected {expected}, got {actual}");
            }
        }
    }

    [Fact]
    public void MergeSliced_YoloXStyle_KeepsBgrOrderAndZeroTo255Range()
    {
        var layout = SliceLayout.Create(CaptureWidth, CaptureHeight, NetSize);
        var baseModel = BuildIdentityModel(NetSize, NetSize, inputName: "images", outputName: "images_out");
        var merged = OnnxPreprocessHead.MergeSliced(baseModel, layout, "images", rescaleTo01: false, rgbChannelOrder: false);

        var nv12 = SyntheticNv12(CaptureWidth, CaptureHeight);
        var referenceFull01 = ReferenceConvertRgb01(nv12, CaptureWidth, CaptureHeight);

        using var session = new InferenceSession(merged);
        var input = new DenseTensor<byte>(nv12, [1, CaptureHeight + CaptureHeight / 2, CaptureWidth]);
        using var results = session.Run([NamedOnnxValue.CreateFromTensor("nv12", input)]);
        var output = results.First(o => o.Name == "images_out").AsTensor<float>();

        for (var slice = 0; slice < 2; slice++)
        {
            var tile = layout.Slices[slice];
            for (var y = 0; y < NetSize; y++)
            for (var x = 0; x < NetSize; x++)
            {
                // BGR order: plane 0 = B, plane 2 = R (the reverse of the D-FINE RGB test above).
                // *255 undoes ReferenceConvertRgb01's own /255 to get back to YOLOX's 0-255 range.
                var expectedB = referenceFull01[2, y, tile.X + x] * 255f;
                var expectedR = referenceFull01[0, y, tile.X + x] * 255f;
                Assert.True(Math.Abs(expectedB - output[slice, 0, y, x]) < 5e-2f, $"B slice {slice} ({x},{y})");
                Assert.True(Math.Abs(expectedR - output[slice, 2, y, x]) < 5e-2f, $"R slice {slice} ({x},{y})");
            }
        }
    }

    [Fact]
    public void MergeSliced_LoadsAndRunsAgainstTheRealDFineModel()
    {
        // Gitignored weights — only runs where one is checked out locally (see fetch_dfine.py).
        var modelPath = FindRepoFile(Path.Combine("models", "dfine_s_obj2coco.onnx"));
        if (modelPath is null) return;

        var layout = SliceLayout.Create(1920, 1080, 640); // 16:9, 2 slices
        var merged = OnnxPreprocessHead.MergeSliced(File.ReadAllBytes(modelPath), layout, "pixel_values",
            rescaleTo01: true, rgbChannelOrder: true);

        using var session = new InferenceSession(merged);
        Assert.Contains(session.InputMetadata.Keys, k => k == "nv12");

        var nv12Rows = layout.CaptureHeight + layout.CaptureHeight / 2;
        var nv12 = new byte[nv12Rows * layout.CaptureWidth];
        for (var i = 0; i < nv12.Length; i++) nv12[i] = (byte)((i * 37 + 11) & 0xFF);
        var input = new DenseTensor<byte>(nv12, [1, nv12Rows, layout.CaptureWidth]);

        using var results = session.Run([NamedOnnxValue.CreateFromTensor("nv12", input)]);
        var logits = results.First(o => o.Name == "logits").AsTensor<float>();
        Assert.Equal(layout.Slices.Count, logits.Dimensions[0]);
        Assert.Equal(300, logits.Dimensions[1]);
        Assert.Equal(80, logits.Dimensions[2]);
    }

    [Fact]
    public void MergeSliced_ComposedWithOnnxBatchAxis_LoadsAndRunsAgainstTheRealYoloXModel()
    {
        var modelPath = FindRepoFile(Path.Combine("models", "yolox_s.onnx"));
        if (modelPath is null) return;

        var layout = SliceLayout.Create(1920, 1080, 640); // 2 slices
        // Composition order matters — see MergeSliced's own doc comment: OnnxBatchAxis must rewrite
        // the model's internal fixed-batch-1 Reshape targets *before* the slicing head is merged in.
        var batchDynamic = OnnxBatchAxis.MakeBatchDynamic(File.ReadAllBytes(modelPath), layout.Slices.Count, numClasses: 80);
        var merged = OnnxPreprocessHead.MergeSliced(batchDynamic, layout, "images", rescaleTo01: false, rgbChannelOrder: false);

        using var session = new InferenceSession(merged);
        Assert.Contains(session.InputMetadata.Keys, k => k == "nv12");

        var nv12Rows = layout.CaptureHeight + layout.CaptureHeight / 2;
        var nv12 = new byte[nv12Rows * layout.CaptureWidth];
        for (var i = 0; i < nv12.Length; i++) nv12[i] = (byte)((i * 37 + 11) & 0xFF);
        var input = new DenseTensor<byte>(nv12, [1, nv12Rows, layout.CaptureWidth]);

        using var results = session.Run([NamedOnnxValue.CreateFromTensor("nv12", input)]);
        var output = results.First().AsTensor<float>();
        Assert.Equal(layout.Slices.Count, output.Dimensions[0]);
        Assert.Equal(85, output.Dimensions[2]); // 5 + 80 classes
    }

    /// <summary>
    /// Regression guard for the 0.186.0 defect where only the first Slice camera on a TensorRT node
    /// worked and every other one failed on every frame with
    /// <c>setInputShape() for input 'nv12'</c>. ONNX Runtime's TensorRT engine cache key is a hash of
    /// the model's file name plus the *names* of the graph inputs and node outputs — no shapes, no
    /// initializer values, and no file name at all for a graph merged in memory. So two heads built
    /// for different capture sizes must not share a name set, or they share a compiled engine that
    /// only fits one of them.
    ///
    /// Asserted on the names rather than by running inference on purpose: the shapes were always
    /// right (both graphs execute correctly on their own), which is exactly why this was invisible
    /// until TensorRT was in the picture.
    /// </summary>
    [Fact]
    public void MergeSliced_TwoCaptureSizes_ShareNoGeneratedNodeNames()
    {
        // Two landscape sources whose slice count is identical (2) — the colliding case. Everything
        // that differs between them is a shape or an initializer value, which the cache key ignores.
        var wide = SliceLayout.Create(CaptureWidth, CaptureHeight, NetSize);
        var narrower = SliceLayout.Create(CaptureWidth - 2, CaptureHeight, NetSize);
        Assert.Equal(wide.Slices.Count, narrower.Slices.Count);
        Assert.NotEqual(wide.CaptureWidth, narrower.CaptureWidth);

        var wideNames = GeneratedValueNames(wide);
        var narrowerNames = GeneratedValueNames(narrower);

        Assert.NotEmpty(wideNames);
        Assert.Empty(wideNames.Intersect(narrowerNames));

        // The graph input stays "nv12" in both — the engines bind it by that literal name, and
        // retagging the node outputs alone is already enough to change the hash.
        var proto = ModelProto.Parser.ParseFrom(
            OnnxPreprocessHead.MergeSliced(BuildIdentityModel(NetSize, NetSize), wide, "pixel_values",
                rescaleTo01: true, rgbChannelOrder: true));
        Assert.Contains(proto.Graph.Input, v => v.Name == "nv12");
    }

    /// <summary>Every value name the sliced head itself introduces for <paramref name="layout"/> —
    /// initializers plus node outputs, minus the two names the head does not own (its <c>nv12</c>
    /// input and the model input it feeds).</summary>
    private static HashSet<string> GeneratedValueNames(SliceLayout layout)
    {
        var merged = OnnxPreprocessHead.MergeSliced(BuildIdentityModel(NetSize, NetSize), layout,
            "pixel_values", rescaleTo01: true, rgbChannelOrder: true);
        var graph = ModelProto.Parser.ParseFrom(merged).Graph;

        var names = new HashSet<string>(graph.Initializer.Select(i => i.Name));
        foreach (var node in graph.Node)
        {
            foreach (var output in node.Output) names.Add(output);
        }
        names.Remove("nv12");
        names.Remove("pixel_values");
        names.Remove("pixel_values_out");
        return names;
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

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

    /// <summary>BT.601 limited-range nv12 -> planar RGB in [0,1], indexed [channel, y, x] — the same
    /// reference formula OnnxPreprocessHeadTests.ReferenceConvert uses (duplicated rather than
    /// shared across test classes, same low-stakes convention test helpers already follow), just
    /// returned as a 3-D array so per-tile sub-regions are easy to index.</summary>
    private static float[,,] ReferenceConvertRgb01(byte[] nv12, int w, int h)
    {
        var outp = new float[3, h, w];
        var plane = w * h;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            float Y = nv12[y * w + x];
            var uvBase = plane;
            var uRow = y / 2;
            var uCol = x / 2;
            float U = nv12[uvBase + uRow * w + uCol * 2];
            float V = nv12[uvBase + uRow * w + uCol * 2 + 1];

            var c = (Y - 16f) * 1.164383f;
            var d = U - 128f;
            var e = V - 128f;
            var r = c + 1.596027f * e;
            var g = c - 0.391762f * d - 0.812968f * e;
            var b = c + 2.017232f * d;

            outp[0, y, x] = Math.Clamp(r, 0f, 255f) / 255f;
            outp[1, y, x] = Math.Clamp(g, 0f, 255f) / 255f;
            outp[2, y, x] = Math.Clamp(b, 0f, 255f) / 255f;
        }
        return outp;
    }

    private static byte[] SyntheticNv12(int w, int h)
    {
        var buf = new byte[w * h + (w * h) / 2];
        for (var i = 0; i < buf.Length; i++)
            buf[i] = (byte)((i * 37 + 11) & 0xFF); // deterministic spread across 0-255
        return buf;
    }

    /// <summary>A minimal ONNX model: <paramref name="inputName"/> float[1,3,H,W] -> Identity ->
    /// <paramref name="outputName"/>. Same shape as OnnxPreprocessHeadTests.BuildIdentityModel; the
    /// declared batch dim of 1 doesn't matter — MergeSliced replaces this input/output's own
    /// metadata entirely (see its own doc comment on forcing a symbolic batch dim).</summary>
    private static byte[] BuildIdentityModel(int w, int h, string inputName = "pixel_values", string outputName = "pixel_values_out")
    {
        static ValueInfoProto Vi(string name, int cc, int hh, int ww) => new()
        {
            Name = name,
            Type = new TypeProto
            {
                TensorType = new TypeProto.Types.Tensor
                {
                    ElemType = (int)TensorProto.Types.DataType.Float,
                    Shape = new TensorShapeProto
                    {
                        Dim =
                        {
                            new TensorShapeProto.Types.Dimension { DimValue = 1 },
                            new TensorShapeProto.Types.Dimension { DimValue = cc },
                            new TensorShapeProto.Types.Dimension { DimValue = hh },
                            new TensorShapeProto.Types.Dimension { DimValue = ww },
                        }
                    }
                }
            }
        };

        var id = new NodeProto { OpType = "Identity", Name = "id" };
        id.Input.Add(inputName);
        id.Output.Add(outputName);

        var graph = new GraphProto { Name = "identity-sliced" };
        graph.Node.Add(id);
        graph.Input.Add(Vi(inputName, 3, h, w));
        graph.Output.Add(Vi(outputName, 3, h, w));

        var model = new ModelProto { IrVersion = 8, ProducerName = "LarisVMS.Tests", Graph = graph };
        model.OpsetImport.Add(new OperatorSetIdProto { Domain = "", Version = 17 });
        return model.ToByteArray();
    }
}
