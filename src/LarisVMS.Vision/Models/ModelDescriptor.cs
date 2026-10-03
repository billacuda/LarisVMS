using System.Text.Json;
using System.Text.Json.Serialization;

namespace LarisVMS.Vision.Models;

/// <summary>
/// Describes how to run and decode one ONNX model dropped into the models directory
/// (<c>C:\ProgramData\LarisVMS\models</c>) — either inferred from the model's own embedded ONNX
/// metadata, or supplied by a same-basename JSON sidecar (<c>yolox-s.onnx</c> + <c>yolox-s.json</c>)
/// when metadata is missing or incomplete. See <see cref="LarisVMS.Vision.Models.ModelDiscovery"/>.
///
/// Ported from SideGlance's <c>Config.ModelDescriptor</c> (now legal to share — both repos are
/// Apache-2.0) and adapted: no <c>Path</c>/<c>Task</c>/<c>GpuId</c>/<c>OpenVinoDevice</c>/
/// <c>YuvMatrix</c>/<c>DefaultSliceOverlap</c> fields (LarisVMS resolves the model's file path from
/// the scan itself, and GPU/backend selection is a node-level concern via <see cref="LarisVMS.Vision.Inference.EngineOptions"/>,
/// not a per-model one). <see cref="Validate"/> covers checks that need only the descriptor plus the
/// containing directory (for path containment); checks that need the loaded ONNX graph happen when
/// the session is actually built.
/// </summary>
public sealed record ModelDescriptor
{
    /// <summary>Display name — defaults to the model file's own base name when not set by a sidecar.</summary>
    public string? Name { get; init; }

    public DecoderKind Decoder { get; init; } = DecoderKind.Auto;

    /// <summary>The model's square input edge. Captured frames are already scaled/padded to exactly
    /// <see cref="InputSize"/> x <see cref="InputSize"/> before reaching the engine (see
    /// <see cref="LarisVMS.Vision.Inference.InferenceProfile"/>).</summary>
    public int InputSize { get; init; }

    /// <summary><c>"RGB"</c> or <c>"BGR"</c> — the channel order the model was trained on.</summary>
    public string ChannelOrder { get; init; } = "RGB";

    public NormalizeSpec Normalize { get; init; } = new();

    public double ConfidenceThreshold { get; init; } = 0.25;

    /// <summary>Used by the Ultralytics decoder's class-aware NMS.</summary>
    public double IouThreshold { get; init; } = 0.45;

    public bool ClassAgnosticNms { get; init; }

    /// <summary>Per-frame detection cap.</summary>
    public int MaxDetections { get; init; } = 300;

    /// <summary><c>"coco80"</c>, or an inline JSON array of label strings. Omit and set
    /// <see cref="LabelsFile"/> instead for a file.</summary>
    public JsonElement? Labels { get; init; }

    /// <summary>Path to a label file (one label per line), relative to the models directory. Must
    /// resolve inside the models directory — see <see cref="TryResolveLabelsFile"/>.</summary>
    public string? LabelsFile { get; init; }

    public BatchSpec Batch { get; init; } = new();

    public TensorRtSpec TensorRt { get; init; } = new();

    /// <summary>Static validation, given the models directory this descriptor was found in (needed to
    /// resolve/contain <see cref="LabelsFile"/>). Returns an empty list when the descriptor is usable;
    /// otherwise a list of plain-language problems, each suitable for a log line and the discovered
    /// model's "metadata not found"/invalid warning.</summary>
    public IReadOnlyList<string> Validate(string modelsDirectory)
    {
        var errors = new List<string>();

        if (!Enum.IsDefined(Decoder))
            errors.Add($"unknown 'decoder': {Decoder}.");

        if (InputSize <= 0 || InputSize % 2 != 0)
            errors.Add($"'inputSize' must be a positive even number (was {InputSize}).");

        if (!ChannelOrder.Equals("RGB", StringComparison.OrdinalIgnoreCase)
            && !ChannelOrder.Equals("BGR", StringComparison.OrdinalIgnoreCase))
            errors.Add($"'channelOrder' must be \"RGB\" or \"BGR\" (was \"{ChannelOrder}\").");

        if (Normalize.Mean.Count != 3)
            errors.Add($"'normalize.mean' must have 3 entries (had {Normalize.Mean.Count}).");
        if (Normalize.Std.Count != 3)
            errors.Add($"'normalize.std' must have 3 entries (had {Normalize.Std.Count}).");
        if (Normalize.Std.Any(s => s == 0))
            errors.Add("'normalize.std' entries must be non-zero.");

        if (ConfidenceThreshold is < 0 or > 1)
            errors.Add($"'confidenceThreshold' must be in [0, 1] (was {ConfidenceThreshold}).");
        if (IouThreshold is < 0 or > 1)
            errors.Add($"'iouThreshold' must be in [0, 1] (was {IouThreshold}).");
        if (MaxDetections <= 0)
            errors.Add($"'maxDetections' must be positive (was {MaxDetections}).");

        if (Batch.Enabled && Batch.MaxBatchSize <= 0)
            errors.Add($"'batch.maxBatchSize' must be positive when batching is enabled (was {Batch.MaxBatchSize}).");

        if (TensorRt.Enabled)
        {
            if (!TensorRt.Precision.Equals("FP16", StringComparison.OrdinalIgnoreCase)
                && !TensorRt.Precision.Equals("FP32", StringComparison.OrdinalIgnoreCase))
                errors.Add($"'tensorRt.precision' must be \"FP16\" or \"FP32\" (was \"{TensorRt.Precision}\").");
            if (TensorRt.WorkspaceMB <= 0)
                errors.Add($"'tensorRt.workspaceMB' must be positive (was {TensorRt.WorkspaceMB}).");
        }

        errors.AddRange(ValidateLabels(modelsDirectory));

        return errors;
    }

    private IEnumerable<string> ValidateLabels(string modelsDirectory)
    {
        var hasInline = Labels is { ValueKind: not JsonValueKind.Null };
        var hasFile = !string.IsNullOrWhiteSpace(LabelsFile);

        if (!hasInline && !hasFile)
        {
            yield return "provide 'labels' (\"coco80\" or an array) or 'labelsFile'.";
            yield break;
        }

        if (hasInline && Labels!.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Array))
            yield return "'labels' must be the string \"coco80\" or an array of label strings.";

        if (hasInline && Labels!.Value.ValueKind == JsonValueKind.Array && Labels.Value.GetArrayLength() == 0)
            yield return "'labels' array is empty.";

        if (hasInline && Labels!.Value.ValueKind == JsonValueKind.String)
        {
            var shorthand = Labels.Value.GetString();
            if (!string.Equals(shorthand, "coco80", StringComparison.OrdinalIgnoreCase))
                yield return $"unknown 'labels' shorthand \"{shorthand}\" (only \"coco80\" is bundled).";
        }

        if (hasFile)
        {
            if (!TryResolveLabelsFile(modelsDirectory, out var resolved, out var containmentError))
                yield return containmentError!;
            else if (!File.Exists(resolved))
                yield return $"labels file not found: {resolved}";
        }
    }

    /// <summary>Resolves <see cref="LabelsFile"/> against <paramref name="modelsDirectory"/> and
    /// rejects anything that escapes it (<c>..</c> traversal, an absolute path elsewhere on disk) —
    /// the models directory is now a location a user drops arbitrary files into, so a sidecar-supplied
    /// path must never be trusted outside it.</summary>
    public bool TryResolveLabelsFile(string modelsDirectory, out string resolvedPath, out string? error)
    {
        resolvedPath = "";
        error = null;
        if (string.IsNullOrWhiteSpace(LabelsFile))
        {
            error = "'labelsFile' is not set.";
            return false;
        }

        var modelsRoot = Path.GetFullPath(modelsDirectory);
        var candidate = Path.GetFullPath(Path.Combine(modelsRoot, LabelsFile));

        var rootWithSeparator = modelsRoot.EndsWith(Path.DirectorySeparatorChar)
            ? modelsRoot
            : modelsRoot + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
            && !candidate.Equals(modelsRoot, StringComparison.OrdinalIgnoreCase))
        {
            error = $"'labelsFile' ({LabelsFile}) resolves outside the models directory — not allowed.";
            return false;
        }

        resolvedPath = candidate;
        return true;
    }
}
