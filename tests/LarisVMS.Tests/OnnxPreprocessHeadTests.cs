using Google.Protobuf;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Onnx;
using LarisVMS.Vision.Inference;

namespace LarisVMS.Tests;

/// <summary>
/// Pass 4a: verifies OnnxPreprocessHead.Merge builds a valid graph whose nv12 -> normalized-RGB
/// output matches a straight C# BT.601 conversion. Runs entirely on the ONNX Runtime CPU EP against
/// a synthetic identity model, so it needs no GPU and no real D-FINE weights.
/// </summary>
public class OnnxPreprocessHeadTests
{
    private const int W = 8;
    private const int H = 8;

    [Fact]
    public void MergedGraph_ProducesBt601NormalizedPlanarRgb()
    {
        var baseModel = BuildIdentityModel(W, H);
        var merged = OnnxPreprocessHead.Merge(baseModel, W, H);

        // The merged model must still parse and must now take nv12, not pixel_values.
        var proto = ModelProto.Parser.ParseFrom(merged);
        Assert.Contains(proto.Graph.Input, v => v.Name == "nv12");
        Assert.DoesNotContain(proto.Graph.Input, v => v.Name == "pixel_values");

        var nv12 = SyntheticNv12(W, H);

        using var session = new InferenceSession(merged);
        var input = new DenseTensor<byte>(nv12, [1, H + H / 2, W]);
        using var results = session.Run([NamedOnnxValue.CreateFromTensor("nv12", input)]);
        var actual = results.First(o => o.Name == "pixel_values_out").AsTensor<float>().ToArray();

        var expected = ReferenceConvert(nv12, W, H);

        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) < 1e-4f,
                $"index {i}: expected {expected[i]}, got {actual[i]}");
    }

    [Fact]
    public void MergedGraph_LoadsAndRunsAgainstTheRealDFineModel()
    {
        // The .onnx weights are gitignored, so this only runs where a model is checked out locally
        // (a dev box that fetched one). It's the end-to-end check that the head splices cleanly into
        // the actual D-FINE graph and ONNX Runtime accepts the result.
        var modelPath = FindRepoFile(Path.Combine("models", "dfine_s_obj2coco.onnx"));
        if (modelPath is null) return;

        const int net = 640;
        var merged = OnnxPreprocessHead.Merge(File.ReadAllBytes(modelPath), net, net);

        using var session = new InferenceSession(merged);
        Assert.Contains(session.InputMetadata.Keys, k => k == "nv12");

        var nv12 = new byte[net * net * 3 / 2];
        for (var i = 0; i < nv12.Length; i++) nv12[i] = (byte)((i * 37 + 11) & 0xFF);
        var input = new DenseTensor<byte>(nv12, [1, net + net / 2, net]);

        using var results = session.Run([NamedOnnxValue.CreateFromTensor("nv12", input)]);
        var logits = results.First(o => o.Name == "logits").AsTensor<float>();
        Assert.Equal(300, logits.Dimensions[1]);
        Assert.Equal(80, logits.Dimensions[2]);
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

    [Fact]
    public void Merge_RejectsAnOpsetBelow13()
    {
        var old = BuildIdentityModel(W, H);
        var proto = ModelProto.Parser.ParseFrom(old);
        proto.OpsetImport.Clear();
        proto.OpsetImport.Add(new OperatorSetIdProto { Domain = "", Version = 11 });

        Assert.Throws<NotSupportedException>(() => OnnxPreprocessHead.Merge(proto.ToByteArray(), W, H));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static float[] ReferenceConvert(byte[] nv12, int w, int h)
    {
        var plane = w * h;
        var outp = new float[3 * plane];
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

            var i = y * w + x;
            outp[i] = Math.Clamp(r, 0f, 255f) / 255f;
            outp[plane + i] = Math.Clamp(g, 0f, 255f) / 255f;
            outp[2 * plane + i] = Math.Clamp(b, 0f, 255f) / 255f;
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

    /// <summary>A minimal ONNX model: <c>pixel_values</c> float[1,3,H,W] -> Identity -> <c>pixel_values_out</c>.</summary>
    private static byte[] BuildIdentityModel(int w, int h)
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
        id.Input.Add("pixel_values");
        id.Output.Add("pixel_values_out");

        var graph = new GraphProto { Name = "identity" };
        graph.Node.Add(id);
        graph.Input.Add(Vi("pixel_values", 3, h, w));
        graph.Output.Add(Vi("pixel_values_out", 3, h, w));

        var model = new ModelProto { IrVersion = 8, ProducerName = "LarisVMS.Tests", Graph = graph };
        model.OpsetImport.Add(new OperatorSetIdProto { Domain = "", Version = 17 });
        return model.ToByteArray();
    }
}
