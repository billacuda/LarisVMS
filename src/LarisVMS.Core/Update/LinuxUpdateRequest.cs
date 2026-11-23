using System.Text.Json;
using System.Text.Json.Serialization;

// LarisVMS.NodeUpdater compiles this same file (it references no other project) under its own
// namespace, so a project that references both (the tests) doesn't see the type twice.
#if LARISVMS_NODEUPDATER
namespace LarisVMS.NodeUpdater.Shared;
#else
namespace LarisVMS.Core.Update;
#endif

/// <summary>
/// How a Linux node or proxy hands a verified update to the root helper. It runs as the unprivileged
/// <c>larisvms</c> user and can't write its own program folder under <c>/opt</c>, so it stages the
/// files in its update folder and writes this request there as <see cref="FileName"/>. A systemd path
/// unit (<c>larisvms-node-update.path</c> / <c>larisvms-proxy-update.path</c>) sees the file and runs
/// <c>LarisVMS.NodeUpdater --linux-apply</c> as root, which checks the files against these hashes (and
/// the publisher's signatures, when it was built with the publisher key), installs them, and restarts
/// the service.
///
/// The staged files always have fixed names (the program's own file name, and
/// <c>onnxruntime-backends.new</c>), so the request carries no paths; which service to restart, and
/// which files it may install, come from the unit's command line, not from here.
/// </summary>
public sealed record LinuxUpdateRequest
{
    public const string FileName = "ready.json";

    /// <summary>Only restart the service (the Nodes or Proxies page's restart button).</summary>
    public bool RestartOnly { get; init; }

    public string? Platform { get; init; }
    public string? Version { get; init; }

    /// <summary>The node or proxy binary, staged under its own program name.</summary>
    public string? Sha256 { get; init; }

    /// <summary>The Vision Service binary, when this update stages one.</summary>
    public string? VisionSha256 { get; init; }

    /// <summary>The publisher's signature over <c>UpdateSignature.UpdatePayload</c>.</summary>
    public string? PublisherSignature { get; init; }

    /// <summary>The unpacked backend tree's stamp (<c>BackendsBundle</c>), when this update stages one
    /// as <c>onnxruntime-backends.new</c>.</summary>
    public string? BackendsStamp { get; init; }

    /// <summary>The publisher's signature over <c>UpdateSignature.BackendsPayload</c>.</summary>
    public string? BackendsPublisherSignature { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, LinuxUpdateRequestJson.Default.LinuxUpdateRequest);

    public static LinuxUpdateRequest? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize(json, LinuxUpdateRequestJson.Default.LinuxUpdateRequest); }
        catch (JsonException) { return null; }
    }

    /// <summary>Writes the request into <paramref name="updateDirectory"/> in one step (a temporary
    /// file renamed over it), so the helper never reads half of one.</summary>
    public void WriteTo(string updateDirectory)
    {
        var path = Path.Combine(updateDirectory, FileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, ToJson());
        File.Move(temp, path, overwrite: true);
    }
}

[JsonSerializable(typeof(LinuxUpdateRequest))]
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class LinuxUpdateRequestJson : JsonSerializerContext;
