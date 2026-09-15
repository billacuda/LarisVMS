using Google.Protobuf;
using LarisVMS.Vision.Inference;
using LarisVMS.Vision.Inference.Decoders;
using LarisVMS.Vision.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Onnx;
using YoloDotNet.Models;

namespace LarisVMS.Tests;

/// <summary>
/// Regression tests for <see cref="GenericOnnxEngine"/>'s opportunistic GPU-native batched Slice path
/// (<c>TryBuildGpuNativeSlicedSession</c>/<c>VerifyBatchedOutputsMatchSingleImage</c>, internal for
/// exactly this purpose via the project's existing <c>InternalsVisibleTo</c>). Confirmed root cause of
/// a production regression: an earlier version trusted "ONNX Runtime didn't throw" as proof the merged
/// graph batches correctly. That is false — ONNX's own <c>Reshape</c>/<c>Gather</c> with a fixed index
/// can silently produce a shape-valid graph whose batch slots all carry one slot's own content (the
/// confirmed real case: Ultralytics' end-to-end NMS head freezes a per-batch index at export trace
/// time). These tests build two tiny in-memory ONNX graphs — a well-behaved one and a deliberately
/// pathological one simulating exactly that bug — using the same Google.Protobuf/Onnx approach
/// <c>OnnxPreprocessHeadSlicedTests</c> already establishes for this codebase's tests.
/// </summary>
public class GenericOnnxEngineSlicedVerificationTests
{
    // InferenceProfile.Create requires a network size that's a positive multiple of 32 (unlike
    // SliceLayout.Create's own looser "positive and even" requirement) — 32 is the smallest that
    // satisfies both, keeping these synthetic-model tests cheap.
    private const int NetSize = 32;
    private const int CaptureWidth = 64;
    private const int CaptureHeight = 32;

    [Fact]
    public void WellBehavedDynamicBatchModel_IsAcceptedAsGpuNative()
    {
        var modelPath = WriteTempModel(BuildDynamicBatchIdentityModel(NetSize, NetSize));
        var logger = new CapturingLogger<GenericOnnxEngine>();
        try
        {
            using var engine = BuildEngine(modelPath, out _, logger);
            Assert.True(engine.IsGpuNativeSlicing, string.Join("\n", logger.Messages));
        }
        finally
        {
            File.Delete(modelPath);
        }
    }

    [Fact]
    public void FrozenBatchIndexModel_FailsVerificationAndFallsBackToCpuPerTile()
    {
        var layout = SliceLayout.Create(CaptureWidth, CaptureHeight, NetSize);
        var modelPath = WriteTempModel(BuildFrozenSlotZeroModel(NetSize, NetSize, layout.Slices.Count));
        try
        {
            using var engine = BuildEngine(modelPath, out _);
            Assert.False(engine.IsGpuNativeSlicing);
        }
        finally
        {
            File.Delete(modelPath);
        }
    }

    [Fact]
    public void StaticBatchOneModel_NeverAttemptsGpuNative()
    {
        // A plain, non-dynamic export (batch hardcoded to 1) — DeclaresPossiblyDynamicBatch should
        // gate the attempt out entirely, same as any ordinary Ultralytics export without dynamic=True.
        var modelPath = WriteTempModel(BuildStaticBatchOneIdentityModel(NetSize, NetSize));
        try
        {
            using var engine = BuildEngine(modelPath, out _);
            Assert.False(engine.IsGpuNativeSlicing);
        }
        finally
        {
            File.Delete(modelPath);
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static GenericOnnxEngine BuildEngine(string modelPath, out SliceLayout layout, Microsoft.Extensions.Logging.ILogger<GenericOnnxEngine>? logger = null)
    {
        layout = SliceLayout.Create(CaptureWidth, CaptureHeight, NetSize);
        var options = new EngineOptions { ModelPath = modelPath };
        var descriptor = new ModelDescriptor
        {
            InputSize = NetSize,
            ChannelOrder = "RGB",
            Normalize = new NormalizeSpec { Scale = 1.0, Mean = [0, 0, 0], Std = [1, 1, 1] },
        };
        var profile = InferenceProfile.Create(NetSize, NetSize, LarisVMS.Core.Enums.AspectMode.Stretch, NetSize);
        return new GenericOnnxEngine(options, descriptor, new NoOpDecoder(), labels: ["object"], profile,
            logger ?? NullLogger<GenericOnnxEngine>.Instance, layout);
    }

    private sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add($"[{logLevel}] {formatter(state, exception)}{(exception is null ? "" : " -- " + exception)}");
    }

    private static string WriteTempModel(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.onnx");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private sealed class NoOpDecoder : IDetectionDecoder
    {
        public List<ObjectDetection> Decode(IReadOnlyCollection<DisposableNamedOnnxValue> outputs,
            IReadOnlyList<string> labels, DecodeThresholds thresholds, InferenceProfile profile, int batchSlot = 0)
            => [];
    }

    private static ValueInfoProto DynamicBatchInput(string name, int c, int h, int w) => new()
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
                        new TensorShapeProto.Types.Dimension { DimParam = "batch" },
                        new TensorShapeProto.Types.Dimension { DimValue = c },
                        new TensorShapeProto.Types.Dimension { DimValue = h },
                        new TensorShapeProto.Types.Dimension { DimValue = w },
                    }
                }
            }
        }
    };

    private static ValueInfoProto StaticOutput(string name, int c, int h, int w) => new()
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
                        new TensorShapeProto.Types.Dimension { DimParam = "batch" },
                        new TensorShapeProto.Types.Dimension { DimValue = c },
                        new TensorShapeProto.Types.Dimension { DimValue = h },
                        new TensorShapeProto.Types.Dimension { DimValue = w },
                    }
                }
            }
        }
    };

    /// <summary>Well-behaved model: <c>pixel_values</c> (dynamic batch) -&gt; Identity -&gt;
    /// <c>pixel_values_out</c>. A genuine batched Concat (built by <c>MergeSliced</c> itself) feeding a
    /// pass-through op is, by construction, numerically identical per-slot to N separate single-tile
    /// runs — the same equivalence <c>OnnxPreprocessHeadSlicedTests</c> already proves directly.</summary>
    private static byte[] BuildDynamicBatchIdentityModel(int w, int h)
    {
        var id = new NodeProto { OpType = "Identity", Name = "id" };
        id.Input.Add("pixel_values");
        id.Output.Add("pixel_values_out");

        var graph = new GraphProto { Name = "dynamic-identity" };
        graph.Node.Add(id);
        graph.Input.Add(DynamicBatchInput("pixel_values", 3, h, w));
        graph.Output.Add(StaticOutput("pixel_values_out", 3, h, w));

        var model = new ModelProto { IrVersion = 8, ProducerName = "LarisVMS.Tests", Graph = graph };
        model.OpsetImport.Add(new OperatorSetIdProto { Domain = "", Version = 17 });
        return model.ToByteArray();
    }

    /// <summary>A plain, non-dynamic model (batch hardcoded to a literal 1) — otherwise identical to
    /// <see cref="BuildDynamicBatchIdentityModel"/>. Represents a normal Ultralytics export without
    /// <c>dynamic=True</c>: <c>DeclaresPossiblyDynamicBatch</c> must gate the GPU-native attempt out
    /// before it ever reaches <c>MergeSliced</c>.</summary>
    private static byte[] BuildStaticBatchOneIdentityModel(int w, int h)
    {
        static ValueInfoProto StaticBatchOneInput(string name, int c, int hh, int ww) => new()
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
                            new TensorShapeProto.Types.Dimension { DimValue = c },
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

        var graph = new GraphProto { Name = "static-identity" };
        graph.Node.Add(id);
        graph.Input.Add(StaticBatchOneInput("pixel_values", 3, h, w));
        graph.Output.Add(StaticOutput("pixel_values_out", 3, h, w));

        var model = new ModelProto { IrVersion = 8, ProducerName = "LarisVMS.Tests", Graph = graph };
        model.OpsetImport.Add(new OperatorSetIdProto { Domain = "", Version = 17 });
        return model.ToByteArray();
    }

    /// <summary>Pathological model simulating the confirmed real bug: <c>pixel_values</c> (dynamic
    /// batch) -&gt; Slice out batch slot 0 only -&gt; Concat that same slot <paramref name="batchCount"/>
    /// times -&gt; <c>pixel_values_out</c>. Shape-valid for any batch size, but every output slot
    /// silently carries slot 0's own content regardless of which physical tile it came from — exactly
    /// what a per-batch index frozen at export trace time produces. Verification must catch this via
    /// slot 1+'s numeric mismatch against its own single-tile run, even though ONNX Runtime itself
    /// never objects to loading or running this graph.</summary>
    private static byte[] BuildFrozenSlotZeroModel(int w, int h, int batchCount)
    {
        var starts = I64("slot0_starts", [0]);
        var ends = I64("slot0_ends", [1]);
        var axes = I64("slot0_axes", [0]);

        var slice = new NodeProto { OpType = "Slice", Name = "slice_slot0" };
        slice.Input.AddRange(["pixel_values", "slot0_starts", "slot0_ends", "slot0_axes"]);
        slice.Output.Add("slot0");

        var concat = new NodeProto { OpType = "Concat", Name = "concat_frozen" };
        for (var i = 0; i < batchCount; i++) concat.Input.Add("slot0");
        concat.Output.Add("pixel_values_out");
        concat.Attribute.Add(new AttributeProto { Name = "axis", Type = AttributeProto.Types.AttributeType.Int, I = 0 });

        var graph = new GraphProto { Name = "frozen-slot-zero" };
        graph.Node.Add(slice);
        graph.Node.Add(concat);
        graph.Input.Add(DynamicBatchInput("pixel_values", 3, h, w));
        graph.Output.Add(StaticOutput("pixel_values_out", 3, h, w));
        graph.Initializer.AddRange([starts, ends, axes]);

        var model = new ModelProto { IrVersion = 8, ProducerName = "LarisVMS.Tests", Graph = graph };
        model.OpsetImport.Add(new OperatorSetIdProto { Domain = "", Version = 17 });
        return model.ToByteArray();
    }

    private static TensorProto I64(string name, long[] values)
    {
        var t = new TensorProto { Name = name, DataType = (int)TensorProto.Types.DataType.Int64 };
        t.Dims.Add(values.Length);
        t.Int64Data.AddRange(values);
        return t;
    }
}
