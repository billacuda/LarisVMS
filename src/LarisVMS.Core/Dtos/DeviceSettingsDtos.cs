namespace LarisVMS.Core.Dtos;

// Camera device settings — encoder, imaging, and audio configuration read from and written to the
// camera itself over ONVIF (ICameraDeviceSettingsService). Independent of whether the camera speaks
// Media1 or Media2: the service normalizes both into these shapes. Throughout, a null value, null range,
// or empty list means the camera didn't report that setting, and the UI hides it.

public record DeviceSettingsRange(double Min, double Max);

public record DeviceSettingsResolution(int Width, int Height);

public record CameraDeviceSettingsDto(
    Guid CameraId,
    string CameraName,
    string? Manufacturer,
    string? Model,
    string? FirmwareVersion,
    IReadOnlyList<VideoStreamSettingsDto> Streams,
    ImageSettingsDto? Image,
    IReadOnlyList<AudioEncoderSettingsDto> Audio,
    // Sections that couldn't be read (e.g. the imaging service faulted) while the rest loaded fine —
    // shown as a note rather than failing the whole page.
    IReadOnlyList<string> Warnings);

/// <summary>One stream's encoder settings. <see cref="SharedWith"/> lists the other stream roles whose
/// profiles use the same encoder configuration on the camera — changing this one changes those too.</summary>
public record VideoStreamSettingsDto(
    string Role,
    string Label,
    string ProfileToken,
    string ConfigToken,
    IReadOnlyList<string> SharedWith,
    VideoEncoderValues Current,
    IReadOnlyList<VideoCodecOptionsDto> Codecs);

/// <summary>Used both for what the camera reports and for a change request — in a change request,
/// every null field is left as the camera has it. RateControl is "CBR" or "VBR".</summary>
public record VideoEncoderValues(
    string? Codec = null,
    string? Profile = null,
    int? Width = null,
    int? Height = null,
    double? FrameRate = null,
    int? BitrateKbps = null,
    string? RateControl = null,
    int? GovLength = null,
    double? Quality = null);

/// <summary>What the camera accepts for one codec. Frame rates come either as a discrete list
/// (<see cref="FrameRates"/>) or a range (<see cref="FrameRateRange"/>), depending on the camera.</summary>
public record VideoCodecOptionsDto(
    string Codec,
    IReadOnlyList<DeviceSettingsResolution> Resolutions,
    IReadOnlyList<double> FrameRates,
    DeviceSettingsRange? FrameRateRange,
    DeviceSettingsRange? BitrateKbps,
    DeviceSettingsRange? GovLength,
    DeviceSettingsRange? Quality,
    IReadOnlyList<string> Profiles,
    bool RateControlSupported);

public record ImageSettingsDto(string VideoSourceToken, ImageSettingsValues Current, ImageSettingsOptionsDto Options);

/// <summary>Used both for what the camera reports and for a change request (null = leave as is).</summary>
public record ImageSettingsValues(
    double? Brightness = null,
    double? Saturation = null,
    double? Contrast = null,
    double? Sharpness = null,
    string? IrCutFilter = null,
    string? WdrMode = null,
    double? WdrLevel = null,
    string? BacklightMode = null,
    double? BacklightLevel = null,
    string? ExposureMode = null,
    string? WhiteBalanceMode = null,
    double? WhiteBalanceCrGain = null,
    double? WhiteBalanceCbGain = null);

public record ImageSettingsOptionsDto(
    DeviceSettingsRange? Brightness,
    DeviceSettingsRange? Saturation,
    DeviceSettingsRange? Contrast,
    DeviceSettingsRange? Sharpness,
    IReadOnlyList<string> IrCutFilterModes,
    IReadOnlyList<string> WdrModes,
    DeviceSettingsRange? WdrLevel,
    IReadOnlyList<string> BacklightModes,
    DeviceSettingsRange? BacklightLevel,
    IReadOnlyList<string> ExposureModes,
    IReadOnlyList<string> WhiteBalanceModes,
    DeviceSettingsRange? WhiteBalanceCrGain,
    DeviceSettingsRange? WhiteBalanceCbGain);

/// <summary>One audio encoder configuration and the stream roles whose profiles use it.</summary>
public record AudioEncoderSettingsDto(
    string ConfigToken,
    IReadOnlyList<string> Roles,
    AudioEncoderValues Current,
    IReadOnlyList<AudioCodecOptionsDto> Codecs);

public record AudioEncoderValues(string? Codec = null, int? BitrateKbps = null, int? SampleRateKhz = null);

public record AudioCodecOptionsDto(string Codec, IReadOnlyList<int> BitratesKbps, IReadOnlyList<int> SampleRatesKhz);

/// <summary>Another camera of the same make and model, offered as a bulk-apply target.</summary>
public record DeviceSettingsPeerDto(Guid CameraId, string Name, string? FirmwareVersion, bool FirmwareDiffers, bool IsEnabled);

/// <summary>One change applied to several cameras of the same make and model. Video is keyed by
/// stream role (Main/Sub/Third), since profile tokens differ between units; Audio applies to the
/// encoder configuration of each camera's Main profile.</summary>
public record BulkDeviceSettingsRequest(
    IReadOnlyList<Guid> CameraIds,
    Dictionary<string, VideoEncoderValues>? Video = null,
    ImageSettingsValues? Image = null,
    AudioEncoderValues? Audio = null);

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum BulkDeviceSettingsStatus { Applied, Partial, Skipped, Failed }

/// <summary>Per-camera outcome of a bulk apply. Partial means some parts applied and others were
/// skipped or failed; <see cref="Messages"/> says which.</summary>
public record BulkDeviceSettingsCameraResult(
    Guid CameraId, string CameraName, BulkDeviceSettingsStatus Status, IReadOnlyList<string> Messages);

/// <summary>A change the camera won't accept — a value outside the options it reported, or a
/// setting it doesn't support. Surfaces as 400.</summary>
public class DeviceSettingsValidationException(string message) : Exception(message);

/// <summary>The camera can't be configured this way at all (no ONVIF device address, no imaging
/// service, no such stream). Surfaces as 409.</summary>
public class DeviceSettingsUnavailableException(string message) : Exception(message);
