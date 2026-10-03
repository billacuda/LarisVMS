using Onnx;

namespace LarisVMS.Vision.Models;

/// <summary>One graph output's name and declared shape — <c>null</c> entries are a dynamic/symbolic
/// dimension.</summary>
public sealed record OnnxOutputInfo(string Name, int?[] Dims);

/// <summary>
/// The bits of an ONNX graph <see cref="ModelDiscovery"/> needs to auto-detect a model's decoder and
/// input contract, read straight from the model bytes via the protobuf parser already vendored in
/// this project for <see cref="OnnxBatchAxis"/>/<see cref="OnnxPreprocessHead"/>
/// (<c>Onnx\onnx-ml.proto</c>, Apache-2.0).
///
/// Adapted from SideGlance's own <c>Inference.Onnx.OnnxModelInfo</c> (which reads only the first
/// input's name/dims and the first output's name) — this version additionally reads
/// <see cref="MetadataProps"/> (needed to auto-detect a descriptor from a model's own embedded
/// metadata) and <b>every</b> graph output's name/dims, not just the first (needed to recognize
/// D-FINE's two-output <c>logits</c>/<c>pred_boxes</c> signature before falling into shape-based YOLO
/// matching — see <see cref="LarisVMS.Vision.Inference.Decoders.DecoderFactory"/>).
/// </summary>
public sealed record OnnxModelInfo(
    string InputName,
    int?[] InputDims,
    IReadOnlyList<OnnxOutputInfo> Outputs,
    IReadOnlyDictionary<string, string> MetadataProps)
{
    public string FirstOutputName => Outputs[0].Name;

    public IReadOnlyList<string> OutputNames => Outputs.Select(o => o.Name).ToArray();

    public static OnnxModelInfo Read(byte[] model)
    {
        var proto = ModelProto.Parser.ParseFrom(model);
        var graph = proto.Graph;

        var input = graph.Input.FirstOrDefault()
            ?? throw new InvalidOperationException("the ONNX graph declares no inputs.");

        var inputDims = ReadDims(input);

        if (graph.Output.Count == 0)
            throw new InvalidOperationException("the ONNX graph declares no outputs.");

        var outputs = graph.Output.Select(o => new OnnxOutputInfo(o.Name, ReadDims(o))).ToArray();

        var metadata = proto.MetadataProps.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        return new OnnxModelInfo(input.Name, inputDims, outputs, metadata);
    }

    private static int?[] ReadDims(ValueInfoProto valueInfo) =>
        (valueInfo.Type?.TensorType?.Shape?.Dim ?? [])
            .Select(d => d.ValueCase == TensorShapeProto.Types.Dimension.ValueOneofCase.DimValue && d.DimValue > 0
                ? (int?)d.DimValue
                : null)
            .ToArray();
}
