using System.Text.Json;
using LarisVMS.Vision.Inference.Decoders;
using Microsoft.Extensions.Logging;

namespace LarisVMS.Vision.Models;

/// <summary>Where a discovered model's <see cref="ModelDescriptor"/> came from.</summary>
public enum ModelMetadataSource
{
    /// <summary>Built entirely from the model's own embedded ONNX <c>metadata_props</c>.</summary>
    OnnxMetadata,

    /// <summary>Built from a same-basename JSON sidecar (<c>yolox-s.onnx</c> + <c>yolox-s.json</c>).</summary>
    JsonSidecar,

    /// <summary>Neither source resolved (or resolution failed validation/decoder-shape checks) — the
    /// model is still listed, but is not selectable/loadable until a sidecar is supplied.</summary>
    Unresolved,
}

/// <summary>One <c>.onnx</c> file found in the models directory, with whatever <see cref="ModelDescriptor"/>
/// and <see cref="IDetectionDecoder"/> could be resolved for it.</summary>
public sealed record DiscoveredModel(
    string Name,
    string OnnxPath,
    ModelDescriptor? Descriptor,
    IDetectionDecoder? Decoder,
    ModelMetadataSource Source,
    IReadOnlyList<string> Warnings)
{
    public bool IsUsable => Source != ModelMetadataSource.Unresolved && Descriptor is not null && Decoder is not null;
}

/// <summary>
/// Scans the models directory (<c>C:\ProgramData\LarisVMS\models</c>) for <c>.onnx</c> files and
/// resolves a <see cref="ModelDescriptor"/> for each — this is the replacement for
/// <see cref="DetectionModelCatalog"/>'s hardcoded family/weights/size switch, and the mechanism that
/// makes any model a user drops in show up in the model dropdown (see the LarisVMS+SideGlance
/// planning notes for the full design).
///
/// Resolution order per model, matching the "read the model first, fall back to a sidecar" design:
/// <list type="number">
///   <item>Try the model's own embedded ONNX <c>metadata_props</c> (see <see cref="TryBuildFromMetadata"/>)
///     — used only if enough keys are present to build a descriptor that passes <see cref="ModelDescriptor.Validate"/>.</item>
///   <item>Else look for a same-basename JSON sidecar (<c>yolox-s.onnx</c> + <c>yolox-s.json</c>, a
///     true co-located sidecar — deliberately not SideGlance's separately-named-file-in-a-config-dir
///     convention). If both exist and disagree, the sidecar wins (more deliberate/explicit), logged.</item>
///   <item>Neither resolves (or resolution fails <see cref="ModelDescriptor.Validate"/> or decoder-shape
///     detection) — the model is still returned (never hidden), tagged <see cref="ModelMetadataSource.Unresolved"/>
///     with a warning naming what's missing.</item>
/// </list>
/// </summary>
public static class ModelDiscovery
{
    private static readonly JsonSerializerOptions SidecarJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static IReadOnlyList<DiscoveredModel> Scan(string modelsDirectory, ILogger logger)
    {
        var results = new List<DiscoveredModel>();
        if (!Directory.Exists(modelsDirectory))
        {
            logger.LogWarning("Models directory {Dir} does not exist yet — no local models available.", modelsDirectory);
            return results;
        }

        foreach (var onnxPath in Directory.EnumerateFiles(modelsDirectory, "*.onnx", SearchOption.TopDirectoryOnly).OrderBy(p => p))
        {
            results.Add(ResolveOne(onnxPath, modelsDirectory, logger));
        }

        return results;
    }

    private static DiscoveredModel ResolveOne(string onnxPath, string modelsDirectory, ILogger logger)
    {
        var name = Path.GetFileNameWithoutExtension(onnxPath);
        var warnings = new List<string>();

        OnnxModelInfo info;
        try
        {
            info = OnnxModelInfo.Read(File.ReadAllBytes(onnxPath));
        }
        catch (Exception ex)
        {
            warnings.Add($"could not read ONNX graph: {ex.Message}");
            return new DiscoveredModel(name, onnxPath, null, null, ModelMetadataSource.Unresolved, warnings);
        }

        var fromMetadata = TryBuildFromMetadata(info.MetadataProps, info, out var metadataDescriptor, out var metadataProblems);
        var sidecarPath = Path.Combine(modelsDirectory, name + ".json");
        var fromSidecar = TryLoadSidecar(sidecarPath, out var sidecarDescriptor, out var sidecarProblems);

        ModelDescriptor? descriptor = null;
        ModelMetadataSource source = ModelMetadataSource.Unresolved;

        if (fromSidecar)
        {
            descriptor = sidecarDescriptor;
            source = ModelMetadataSource.JsonSidecar;
            if (fromMetadata)
                logger.LogWarning(
                    "Model {Name} has both embedded ONNX metadata and a sidecar ({Sidecar}) — the sidecar takes " +
                    "precedence.", name, sidecarPath);
        }
        else if (fromMetadata)
        {
            descriptor = metadataDescriptor;
            source = ModelMetadataSource.OnnxMetadata;
        }
        else
        {
            warnings.Add($"metadata not found — supply a {name}.json sidecar.");
            warnings.AddRange(metadataProblems);
            warnings.AddRange(sidecarProblems);
            return new DiscoveredModel(name, onnxPath, null, null, ModelMetadataSource.Unresolved, warnings);
        }

        var validationErrors = descriptor!.Validate(modelsDirectory);
        if (validationErrors.Count > 0)
        {
            warnings.AddRange(validationErrors);
            return new DiscoveredModel(name, onnxPath, descriptor, null, ModelMetadataSource.Unresolved, warnings);
        }

        try
        {
            var labelCount = ResolveLabelCount(descriptor, modelsDirectory);
            var decoder = DecoderFactory.Resolve(descriptor.Decoder, info, labelCount);
            return new DiscoveredModel(descriptor.Name ?? name, onnxPath, descriptor, decoder, source, warnings);
        }
        catch (Exception ex)
        {
            warnings.Add($"decoder could not be resolved: {ex.Message}");
            return new DiscoveredModel(name, onnxPath, descriptor, null, ModelMetadataSource.Unresolved, warnings);
        }
    }

    private static int ResolveLabelCount(ModelDescriptor descriptor, string modelsDirectory)
    {
        if (descriptor.Labels is { ValueKind: JsonValueKind.Array } arr) return arr.GetArrayLength();
        if (descriptor.Labels is { ValueKind: JsonValueKind.String } s
            && string.Equals(s.GetString(), "coco80", StringComparison.OrdinalIgnoreCase))
            return 80;
        if (!string.IsNullOrWhiteSpace(descriptor.LabelsFile)
            && descriptor.TryResolveLabelsFile(modelsDirectory, out var resolved, out _)
            && File.Exists(resolved))
            return File.ReadLines(resolved).Count(l => !string.IsNullOrWhiteSpace(l));
        throw new InvalidOperationException("could not determine label count from 'labels'/'labelsFile'.");
    }

    /// <summary>Tries each recognized embedded-metadata convention in turn, first one that builds a
    /// descriptor wins. Real-world exporters don't write LarisVMS's own bespoke keys (confirmed: not
    /// even this project's own <c>fetch_dfine.py</c>/<c>fetch_yolox.py</c> do), so the LarisVMS
    /// convention alone left essentially every dropped-in model unresolved — Ultralytics' own
    /// real, documented embedded-metadata convention (<see cref="TryBuildFromUltralyticsMetadata"/>)
    /// is what actually lets a standard YOLOv8/11/26-family export resolve with no sidecar.</summary>
    private static bool TryBuildFromMetadata(IReadOnlyDictionary<string, string> props, OnnxModelInfo info,
        out ModelDescriptor? descriptor, out IReadOnlyList<string> problems)
    {
        if (TryBuildFromLarisVmsMetadata(props, out descriptor, out problems)) return true;
        var larisVmsProblems = problems;

        if (TryBuildFromUltralyticsMetadata(props, info, out descriptor, out problems)) return true;

        var combined = new List<string>(larisVmsProblems);
        combined.AddRange(problems);
        problems = combined;
        return false;
    }

    /// <summary>Small, LarisVMS-specific key convention for describing a model purely from its own
    /// embedded ONNX <c>metadata_props</c> — flat string keys, since that's all the ONNX IR allows.
    /// Requires at least <c>decoder</c>, <c>input_size</c>, and <c>labels</c> to attempt building a
    /// descriptor at all; anything else missing falls back to defaults on <see cref="ModelDescriptor"/>
    /// itself and is caught by <see cref="ModelDescriptor.Validate"/> if it matters. A power-user
    /// convention for someone hand-embedding metadata via their own export step — most real exports
    /// don't write these keys, which is exactly why <see cref="TryBuildFromUltralyticsMetadata"/>
    /// exists alongside it.</summary>
    private static bool TryBuildFromLarisVmsMetadata(IReadOnlyDictionary<string, string> props, out ModelDescriptor? descriptor,
        out IReadOnlyList<string> problems)
    {
        descriptor = null;
        var localProblems = new List<string>();
        problems = localProblems;

        if (!props.TryGetValue("decoder", out var decoderStr)
            || !props.TryGetValue("input_size", out var inputSizeStr)
            || !props.TryGetValue("labels", out var labelsStr))
            return false;

        if (!Enum.TryParse<DecoderKind>(decoderStr, ignoreCase: true, out var decoderKind))
        {
            localProblems.Add($"embedded metadata 'decoder' value \"{decoderStr}\" is not recognized.");
            return false;
        }

        if (!int.TryParse(inputSizeStr, out var inputSize))
        {
            localProblems.Add($"embedded metadata 'input_size' value \"{inputSizeStr}\" is not an integer.");
            return false;
        }

        JsonElement? labels;
        if (string.Equals(labelsStr, "coco80", StringComparison.OrdinalIgnoreCase))
        {
            labels = JsonSerializer.SerializeToElement("coco80");
        }
        else
        {
            try { labels = JsonSerializer.Deserialize<JsonElement>(labelsStr); }
            catch (JsonException)
            {
                localProblems.Add("embedded metadata 'labels' is neither \"coco80\" nor a valid JSON array.");
                return false;
            }
        }

        descriptor = new ModelDescriptor
        {
            Decoder = decoderKind,
            InputSize = inputSize,
            Labels = labels,
            ChannelOrder = props.GetValueOrDefault("channel_order", "RGB"),
            ConfidenceThreshold = props.TryGetValue("confidence_threshold", out var c) && double.TryParse(c, out var cv) ? cv : 0.25,
            IouThreshold = props.TryGetValue("iou_threshold", out var i) && double.TryParse(i, out var iv) ? iv : 0.45,
            ClassAgnosticNms = props.TryGetValue("class_agnostic_nms", out var a) && bool.TryParse(a, out var av) && av,
            MaxDetections = props.TryGetValue("max_detections", out var m) && int.TryParse(m, out var mv) ? mv : 300,
        };
        return true;
    }

    /// <summary>Ultralytics' own real embedded-metadata convention (<c>model.export(format="onnx")</c>,
    /// covering YOLOv8/11/26 and any fine-tune of them — the most likely thing a user drops in). Needs
    /// only <c>names</c> (a <c>str()</c> of the model's Python <c>{0: 'person', 1: 'bicycle', ...}</c>
    /// class dict — not JSON, hence <see cref="ParseUltralyticsNames"/>) to get labels; input size
    /// comes from <c>imgsz</c> (<c>str()</c> of <c>[height, width]</c>) if present, else falls back to
    /// whatever fixed square size the ONNX graph itself declares
    /// (<see cref="TryGetGraphDeclaredSquareInputSize"/>) — independent of any metadata convention,
    /// since many exports (including this project's own D-FINE/YOLOX) bake a fixed input size directly
    /// into the graph regardless of what metadata says. Decoder is left <see cref="DecoderKind.Auto"/>:
    /// no metadata key supplies it, and <see cref="Decoders.DecoderFactory"/>'s existing shape-based
    /// detection already resolves it from the graph alone.</summary>
    private static bool TryBuildFromUltralyticsMetadata(IReadOnlyDictionary<string, string> props, OnnxModelInfo info,
        out ModelDescriptor? descriptor, out IReadOnlyList<string> problems)
    {
        descriptor = null;
        var localProblems = new List<string>();
        problems = localProblems;

        if (!props.TryGetValue("names", out var namesRaw))
            return false;

        var labels = ParseUltralyticsNames(namesRaw);
        if (labels is null || labels.Count == 0)
        {
            localProblems.Add("embedded Ultralytics metadata 'names' could not be parsed.");
            return false;
        }

        int? inputSize = props.TryGetValue("imgsz", out var imgszRaw) ? ParseUltralyticsImgsz(imgszRaw) : null;
        inputSize ??= TryGetGraphDeclaredSquareInputSize(info);

        if (inputSize is not { } size || size <= 0)
        {
            localProblems.Add(
                "could not determine input size from embedded metadata ('imgsz') or the model's own graph " +
                "(no fixed square input declared) — supply a sidecar JSON with 'inputSize' set.");
            return false;
        }

        descriptor = new ModelDescriptor
        {
            Decoder = DecoderKind.Auto,
            InputSize = size,
            Labels = JsonSerializer.SerializeToElement(labels),
            ChannelOrder = "RGB",
        };
        return true;
    }

    /// <summary>Parses Ultralytics' <c>str()</c>-of-a-Python-dict <c>names</c> value, e.g.
    /// <c>"{0: 'person', 1: 'bicycle', 2: 'car'}"</c>, into an ordered label list — deliberately not
    /// <see cref="JsonSerializer"/>, since this is Python repr syntax (single-quoted, unquoted integer
    /// keys), not JSON. Returns null on anything that doesn't match the expected shape rather than
    /// guessing at a malformed value.</summary>
    internal static IReadOnlyList<string>? ParseUltralyticsNames(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '{' || trimmed[^1] != '}') return null;

        var byIndex = new SortedDictionary<int, string>();
        foreach (var entry in SplitTopLevel(trimmed[1..^1], ','))
        {
            var colon = entry.IndexOf(':');
            if (colon < 0) return null;

            if (!int.TryParse(entry[..colon].Trim(), out var index)) return null;
            var value = UnquotePythonString(entry[(colon + 1)..].Trim());
            if (value is null) return null;

            byIndex[index] = value;
        }

        return byIndex.Count > 0 ? byIndex.Values.ToList() : null;
    }

    /// <summary>Extracts the first integer out of Ultralytics' <c>imgsz</c> value, e.g.
    /// <c>"[640, 640]"</c> or a bare <c>"640"</c> — <see cref="ModelDescriptor.InputSize"/> is
    /// square-only, so a non-square <c>[h, w]</c> just takes the first (height) dimension.</summary>
    private static int? ParseUltralyticsImgsz(string raw)
    {
        var match = System.Text.RegularExpressions.Regex.Match(raw, @"\d+");
        return match.Success ? int.Parse(match.Value) : null;
    }

    /// <summary>The square edge a fixed NCHW input declares (dims[2] == dims[3], both non-dynamic), or
    /// null when the graph's input is dynamic or non-square — a metadata-independent fallback source
    /// for <see cref="ModelDescriptor.InputSize"/>, since many real exports bake this in directly.</summary>
    private static int? TryGetGraphDeclaredSquareInputSize(OnnxModelInfo info) =>
        info.InputDims.Length == 4 && info.InputDims[2] is { } h && info.InputDims[3] is { } w && h == w && h > 0
            ? h
            : null;

    /// <summary>Splits on <paramref name="separator"/> only outside of a quoted (single or double)
    /// span — a plain <c>string.Split</c> would break on a label name that itself contains the
    /// separator (rare, but real label sets aren't guaranteed not to).</summary>
    private static List<string> SplitTopLevel(string s, char separator)
    {
        var result = new List<string>();
        var start = 0;
        char? quote = null;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (quote is { } q) { if (c == q) quote = null; continue; }
            if (c is '\'' or '"') { quote = c; continue; }
            if (c == separator) { result.Add(s[start..i]); start = i + 1; }
        }
        result.Add(s[start..]);
        return result;
    }

    private static string? UnquotePythonString(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && ((s[0] == '\'' && s[^1] == '\'') || (s[0] == '"' && s[^1] == '"'))
            ? s[1..^1]
            : null;
    }

    private static bool TryLoadSidecar(string sidecarPath, out ModelDescriptor? descriptor, out IReadOnlyList<string> problems)
    {
        descriptor = null;
        problems = [];
        if (!File.Exists(sidecarPath)) return false;

        try
        {
            var json = File.ReadAllText(sidecarPath);
            descriptor = JsonSerializer.Deserialize<ModelDescriptor>(json, SidecarJsonOptions);
            return descriptor is not null;
        }
        catch (Exception ex)
        {
            problems = [$"sidecar {sidecarPath} could not be parsed: {ex.Message}"];
            return false;
        }
    }
}
