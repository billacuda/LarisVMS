using System.Xml.Linq;

namespace LarisVMS.Onvif.Clients;

public record OnvifRange(double Min, double Max);

public record OnvifResolution(int Width, int Height);

/// <summary>One video encoder configuration as the device currently has it, normalized across Media
/// (ver10) and Media2 (ver20). <see cref="ConstantBitRate"/> is only ever non-null from Media2 — Media1
/// has no standard CBR/VBR field. <see cref="Raw"/> is the configuration element exactly as received:
/// Set operations patch a copy of it rather than rebuilding it, so every element this record doesn't
/// model (Multicast, SessionTimeout, vendor extensions) goes back to the device unchanged.</summary>
public record OnvifVideoEncoderConfig(
    string Token, string? Name, int UseCount, string? Encoding, OnvifResolution? Resolution,
    double? Quality, double? FrameRateLimit, int? EncodingInterval, int? BitrateLimitKbps,
    bool? ConstantBitRate, int? GovLength, string? Profile)
{
    internal XElement Raw { get; init; } = new("Configuration");
}

/// <summary>What the device accepts for one encoding (H264, H265, JPEG, …). Media1 reports frame
/// rates as a range, Media2 as a discrete list — whichever the device sent is filled, the other is
/// empty/null. An empty list or null range means the device didn't report that option at all.</summary>
public record OnvifVideoEncodingOptions(
    string Encoding,
    IReadOnlyList<OnvifResolution> Resolutions,
    OnvifRange? FrameRateRange,
    IReadOnlyList<double> FrameRatesSupported,
    OnvifRange? BitrateRangeKbps,
    OnvifRange? GovLengthRange,
    OnvifRange? QualityRange,
    IReadOnlyList<string> Profiles,
    bool ConstantBitRateSupported);

/// <summary>A partial change: every null field is left as the device currently has it.</summary>
public record OnvifVideoEncoderChange(
    string? Encoding = null, OnvifResolution? Resolution = null, double? FrameRateLimit = null,
    int? BitrateLimitKbps = null, bool? ConstantBitRate = null, int? GovLength = null,
    string? Profile = null, double? Quality = null);

public record OnvifAudioEncoderConfig(string Token, string? Name, string? Encoding, int? BitrateKbps, int? SampleRateKhz)
{
    internal XElement Raw { get; init; } = new("Configuration");
}

public record OnvifAudioEncodingOptions(string Encoding, IReadOnlyList<int> BitratesKbps, IReadOnlyList<int> SampleRatesKhz);

public record OnvifAudioEncoderChange(string? Encoding = null, int? BitrateKbps = null, int? SampleRateKhz = null);

/// <summary>ver20 imaging settings for one video source. Every field is nullable: a device reports
/// only the adjustments it actually has.</summary>
public record OnvifImagingSettings(
    double? Brightness, double? ColorSaturation, double? Contrast, double? Sharpness,
    string? IrCutFilter,
    string? WideDynamicRangeMode, double? WideDynamicRangeLevel,
    string? BacklightCompensationMode, double? BacklightCompensationLevel,
    string? ExposureMode,
    string? WhiteBalanceMode, double? WhiteBalanceCrGain, double? WhiteBalanceCbGain)
{
    internal XElement Raw { get; init; } = new("ImagingSettings");
}

public record OnvifImagingOptions(
    OnvifRange? Brightness, OnvifRange? ColorSaturation, OnvifRange? Contrast, OnvifRange? Sharpness,
    IReadOnlyList<string> IrCutFilterModes,
    IReadOnlyList<string> WideDynamicRangeModes, OnvifRange? WideDynamicRangeLevel,
    IReadOnlyList<string> BacklightCompensationModes, OnvifRange? BacklightCompensationLevel,
    IReadOnlyList<string> ExposureModes,
    IReadOnlyList<string> WhiteBalanceModes, OnvifRange? WhiteBalanceCrGain, OnvifRange? WhiteBalanceCbGain);

/// <summary>A partial change: null fields are not sent.</summary>
public record OnvifImagingChange(
    double? Brightness = null, double? ColorSaturation = null, double? Contrast = null, double? Sharpness = null,
    string? IrCutFilter = null,
    string? WideDynamicRangeMode = null, double? WideDynamicRangeLevel = null,
    string? BacklightCompensationMode = null, double? BacklightCompensationLevel = null,
    string? ExposureMode = null,
    string? WhiteBalanceMode = null, double? WhiteBalanceCrGain = null, double? WhiteBalanceCbGain = null);
