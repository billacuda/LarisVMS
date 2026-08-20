namespace LarisVMS.Media;

/// <summary>
/// M17 foundation: the decode → filter → encode ffmpeg argument shape every hardware-transcode
/// consumer downstream (M18's privacy-mask burn-in and adaptive-streaming downscale, M22's archive
/// downscale) builds on, so each of those is "pick a filter chain and call this" rather than a new
/// pipeline invented per feature. M18's privacy-mask burn-in (RecordingSession, driven by
/// PrivacyMaskFilterBuilder + EncoderSelection below) is the first real caller.
/// </summary>
public enum EncoderFamily
{
    Software,
    IntelQsv,
    NvidiaNvenc,
    AmdAmf
}

/// <summary>Maps one of <see cref="FfmpegCapabilityProber.KnownEncoders"/> to the family it belongs
/// to — the family decides which decode-side `-hwaccel` (if any) pairs correctly with it, see
/// <see cref="EncodePipeline.DecodeHwaccelArgs"/>.</summary>
public static class EncoderFamilies
{
    public static EncoderFamily For(string encoderName) => encoderName switch
    {
        "h264_qsv" or "hevc_qsv" => EncoderFamily.IntelQsv,
        "h264_nvenc" or "hevc_nvenc" => EncoderFamily.NvidiaNvenc,
        "h264_amf" or "hevc_amf" => EncoderFamily.AmdAmf,
        _ => EncoderFamily.Software
    };
}

/// <param name="EncoderName">One of FfmpegCapabilityProber.KnownEncoders — the caller picks this
/// from a node's Node.DetectedEncodersJson, not a fixed default, since which hardware (if any) a
/// given node has varies.</param>
/// <param name="VideoFilters">Raw `-vf` filter expressions (e.g. "scale=1280:-2", or a mask feature's
/// own "drawbox=..." chain), joined with ',' in the order given. Null/empty means no filtering — a
/// transcode that only changes the encoder, not the picture.</param>
/// <param name="Preset">Encoder-specific speed/quality preset (e.g. "veryfast" for libx264,
/// "fast" for nvenc/qsv/amf) — presets aren't a shared vocabulary across encoders, so this is passed
/// through verbatim rather than this class trying to normalize one.</param>
/// <param name="Crf">Software-encoder constant-quality knob (libx264/libx265's `-crf`, 0-51, lower is
/// higher quality) — meaningless for a hardware encoder, which uses BitrateKbps instead.</param>
/// <param name="BitrateKbps">Target video bitrate for a hardware encoder's `-b:v` — hardware encoders
/// on this app's supported list don't offer a CRF-equivalent constant-quality mode as reliably as
/// libx264/libx265 do, so bitrate targeting is the common denominator for them.</param>
public record EncodePipelineOptions(
    string EncoderName,
    IReadOnlyList<string>? VideoFilters = null,
    string? Preset = null,
    int? Crf = null,
    int? BitrateKbps = null);

public static class EncodePipeline
{
    /// <summary>The decode-side `-hwaccel` flag (if any) that actually pairs with the given encoder
    /// family, meant to be placed before `-i` on the ffmpeg command line the same way RecordingSession
    /// places `-rtsp_transport`. Returned as a full flag pair (e.g. ["-hwaccel", "qsv"]) rather than a
    /// bare value so a caller with nothing to add can just concat an empty list.
    ///
    /// AmdAmf intentionally returns empty: unlike QSV/NVENC, ffmpeg's AMF decode-hwaccel support on
    /// Windows is inconsistent enough across driver/build combinations that guessing one here risks
    /// being wrong more often than it helps — an AMF *encode* still gets the hardware encoder even
    /// with software decode feeding it, just not a fully hardware pipeline. Revisit against real AMD
    /// hardware once M18 actually wires this up; this mapping has not been validated on real hardware
    /// for any family, only checked against ffmpeg's own documented flag names.</summary>
    public static IReadOnlyList<string> DecodeHwaccelArgs(EncoderFamily family) => family switch
    {
        EncoderFamily.IntelQsv => ["-hwaccel", "qsv"],
        EncoderFamily.NvidiaNvenc => ["-hwaccel", "cuda"],
        _ => []
    };

    /// <summary>The `-preset` value for a real-time (recording-speed, not archival-quality) encode
    /// with the given encoder — every consumer of this pipeline so far (M18's privacy-mask burn-in)
    /// is encoding a live RTSP feed in real time, so "fast enough to keep up with the camera's own
    /// frame rate" is the only preset choice that matters, not a caller-tunable knob yet.
    ///
    /// AMD AMF returns null (no `-preset` flag added) rather than a guess: AMF doesn't use ffmpeg's
    /// generic `-preset` option at all — it has its own `-quality`/`-usage` flags — so passing one
    /// through would be a silently-ignored or outright-rejected argument depending on the ffmpeg
    /// build. Revisit once this runs against real AMD hardware.</summary>
    public static string? RealtimePreset(string encoderName) => EncoderFamilies.For(encoderName) switch
    {
        EncoderFamily.Software => "veryfast",
        EncoderFamily.IntelQsv => "fast",
        // "fast" rather than a p1-p7 numeric preset: ffmpeg's h264_nvenc/hevc_nvenc still accept the
        // legacy named presets as aliases in current builds, which is more portable across ffmpeg/
        // driver version combinations than guessing which numeric preset a given build maps them to.
        EncoderFamily.NvidiaNvenc => "fast",
        EncoderFamily.AmdAmf => null,
        _ => null
    };

    /// <summary>Builds just the filter+encode arguments (everything from an optional `-vf` through
    /// the encoder's own quality knobs) — deliberately not the whole command line, since where this
    /// slots in (single-output transcode vs. one leg of a tee alongside an unmodified `-c copy` leg,
    /// as privacy masking will need) is a decision each consumer makes for itself.</summary>
    public static IReadOnlyList<string> BuildArgs(EncodePipelineOptions options)
    {
        var args = new List<string>();

        if (options.VideoFilters is { Count: > 0 })
        {
            args.Add("-vf");
            args.Add(string.Join(',', options.VideoFilters));
        }

        args.Add("-c:v");
        args.Add(options.EncoderName);

        if (!string.IsNullOrEmpty(options.Preset))
        {
            args.Add("-preset");
            args.Add(options.Preset);
        }

        if (options.Crf is { } crf)
        {
            args.Add("-crf");
            args.Add(crf.ToString());
        }

        if (options.BitrateKbps is { } bitrateKbps)
        {
            args.Add("-b:v");
            args.Add($"{bitrateKbps}k");
        }

        return args;
    }
}

/// <summary>
/// M18: which encoder a transcode consumer should actually ask for, given a node's own detected
/// list. Always targets H.264 output regardless of the camera's source codec — this app's MSE
/// playback path already lists AVC candidates first (see playback-player.js's pickMimeType), and
/// trying to preserve HEVC through a mask/downscale transcode would mean checking HEVC hardware
/// encoder support separately from H.264's, real complexity for a difference that doesn't matter once
/// the stream is being re-encoded anyway. libx264 (software) is always in the list this switches on —
/// it ships in essentially every ffmpeg build — so this only returns null when detectedEncoders
/// itself is empty (the probe hasn't run yet, or failed).
/// </summary>
public static class EncoderSelection
{
    public static string? ChooseH264Encoder(IReadOnlyList<string> detectedEncoders)
    {
        // Hardware first, in the order a real encode is most likely to be both available and fast on
        // typical recorder hardware — NVENC and QSV are both mature/widely deployed, AMF less so.
        foreach (var candidate in new[] { "h264_nvenc", "h264_qsv", "h264_amf", "libx264" })
        {
            if (detectedEncoders.Contains(candidate)) return candidate;
        }
        return null;
    }
}
