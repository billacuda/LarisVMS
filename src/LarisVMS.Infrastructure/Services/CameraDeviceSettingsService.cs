using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Onvif.Clients;
using LarisVMS.Onvif.Soap;

namespace LarisVMS.Infrastructure.Services;

/// <summary>
/// See ICameraDeviceSettingsService. Encoder settings go through Media2 whenever the camera
/// advertises it (the only standard source of H.265 and the CBR/VBR flag) and fall back to Media1 if a
/// Media2 call faults. Encoder configuration tokens come from a live Media1 GetProfiles every time —
/// they aren't stored, and the stream rows only carry profile tokens.
///
/// Bulk apply loads every camera from the database up front and then talks to the cameras in parallel
/// without touching the DbContext (it isn't thread-safe). The re-probes that refresh each camera's
/// stream rows run one at a time afterwards.
/// </summary>
public class CameraDeviceSettingsService(ApplicationDbContext db, Func<HttpClient> httpClientFactory, ICameraService cameraService)
    : ICameraDeviceSettingsService
{
    private const int BulkParallelism = 4;

    internal sealed record StreamRef(CameraStreamRole Role, string ProfileToken, string Label);

    internal sealed record Target(
        Guid CameraId, string Name, OnvifCredentials? Credentials,
        Uri Media, Uri? Media2, Uri? Imaging, string? VideoSourceToken,
        IReadOnlyList<StreamRef> Streams);

    private sealed record EncoderState(OnvifVideoEncoderConfig Config, IReadOnlyList<OnvifVideoEncodingOptions> Options, bool ViaMedia2);

    // ── Read ────────────────────────────────────────────────────────────────────────────────────

    public async Task<CameraDeviceSettingsDto?> GetAsync(Guid cameraId, CancellationToken ct = default)
    {
        var camera = await LoadCameraAsync(cameraId, ct);
        if (camera is null) return null;
        var target = ToTarget(camera);

        var soap = NewSoap();
        var profiles = await new OnvifMediaClient(soap).GetProfilesAsync(target.Media, target.Credentials, ct);
        var warnings = new List<string>();

        // Video: one read per distinct encoder configuration, since several profiles can share one.
        var encoderCache = new Dictionary<string, EncoderState?>();
        var streams = new List<VideoStreamSettingsDto>();
        foreach (var stream in target.Streams)
        {
            var profile = FindProfile(profiles, stream.ProfileToken);
            if (profile?.VideoEncoderConfigurationToken is not { } configToken) continue;

            if (!encoderCache.TryGetValue(configToken, out var state))
            {
                try { state = await ReadEncoderAsync(soap, target, configToken, profile.Token, ct); }
                catch (OnvifFaultException ex)
                {
                    state = null;
                    warnings.Add($"Couldn't read the {stream.Label} stream's encoder settings: {ex.Message}");
                }
                encoderCache[configToken] = state;
            }
            if (state is null || state.Options.Count == 0) continue;

            var sharedWith = target.Streams
                .Where(s => s.Role != stream.Role && FindProfile(profiles, s.ProfileToken)?.VideoEncoderConfigurationToken == configToken)
                .Select(s => s.Label).ToList();
            streams.Add(new VideoStreamSettingsDto(
                stream.Role.ToString(), stream.Label, stream.ProfileToken, configToken, sharedWith,
                ToValues(state.Config), state.Options.Select(o => ToDto(o, state.ViaMedia2)).ToList()));
        }

        ImageSettingsDto? image = null;
        if (target.Imaging is { } imagingUri && SourceToken(target, profiles) is { } sourceToken)
        {
            try
            {
                var imaging = new OnvifImagingClient(soap);
                var settings = await imaging.GetImagingSettingsAsync(imagingUri, sourceToken, target.Credentials, ct);
                var options = await imaging.GetOptionsAsync(imagingUri, sourceToken, target.Credentials, ct);
                var dto = new ImageSettingsDto(sourceToken, ToValues(settings), ToDto(options));
                if (HasAnyImageOption(dto.Options)) image = dto;
            }
            catch (OnvifFaultException ex) { warnings.Add($"Couldn't read the image settings: {ex.Message}"); }
        }

        var audio = new List<AudioEncoderSettingsDto>();
        var audioTokens = target.Streams
            .Select(s => (Stream: s, Token: FindProfile(profiles, s.ProfileToken)?.AudioEncoderConfigurationToken))
            .Where(x => x.Token is not null)
            .GroupBy(x => x.Token!);
        foreach (var group in audioTokens)
        {
            try
            {
                var media = new OnvifMediaClient(soap);
                var config = await media.GetAudioEncoderConfigurationAsync(target.Media, group.Key, target.Credentials, ct);
                var options = await media.GetAudioEncoderConfigurationOptionsAsync(target.Media, group.Key, group.First().Stream.ProfileToken, target.Credentials, ct);
                if (options.Count == 0) continue;
                audio.Add(new AudioEncoderSettingsDto(group.Key, group.Select(x => x.Stream.Label).ToList(),
                    new AudioEncoderValues(config.Encoding, config.BitrateKbps, config.SampleRateKhz),
                    options.Select(o => new AudioCodecOptionsDto(o.Encoding, o.BitratesKbps, o.SampleRatesKhz)).ToList()));
            }
            catch (OnvifFaultException ex) { warnings.Add($"Couldn't read the audio settings: {ex.Message}"); }
        }

        return new CameraDeviceSettingsDto(camera.Id, camera.Name, camera.Manufacturer, camera.Model, camera.FirmwareVersion,
            streams, image, audio, warnings);
    }

    // ── Single-camera changes ───────────────────────────────────────────────────────────────────

    public async Task UpdateVideoAsync(Guid cameraId, string role, VideoEncoderValues change, CancellationToken ct = default)
    {
        var target = await LoadTargetAsync(cameraId, ct);
        var soap = NewSoap();
        var profiles = await new OnvifMediaClient(soap).GetProfilesAsync(target.Media, target.Credentials, ct);
        await ApplyVideoAsync(soap, target, profiles, role, change, ct);
        // Refreshes the stream rows (resolution, codec, fps, bitrate) the node and the rest of the UI read.
        await cameraService.ProbeAsync(cameraId, ct);
    }

    public async Task UpdateImageAsync(Guid cameraId, ImageSettingsValues change, CancellationToken ct = default)
    {
        var target = await LoadTargetAsync(cameraId, ct);
        var soap = NewSoap();
        var profiles = await new OnvifMediaClient(soap).GetProfilesAsync(target.Media, target.Credentials, ct);
        await ApplyImageAsync(soap, target, profiles, change, ct);
    }

    public async Task UpdateAudioAsync(Guid cameraId, string configToken, AudioEncoderValues change, CancellationToken ct = default)
    {
        var target = await LoadTargetAsync(cameraId, ct);
        var soap = NewSoap();
        var profiles = await new OnvifMediaClient(soap).GetProfilesAsync(target.Media, target.Credentials, ct);
        await ApplyAudioAsync(soap, target, profiles, configToken, change, ct);
        await cameraService.ProbeAsync(cameraId, ct);
    }

    // ── Bulk ────────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DeviceSettingsPeerDto>> GetSameModelPeersAsync(Guid cameraId, CancellationToken ct = default)
    {
        var camera = await db.Cameras.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cameraId, ct);
        if (camera is null || ModelKey(camera) is not { } key) return [];

        var candidates = await db.Cameras.AsNoTracking()
            .Where(c => c.Id != cameraId && c.Manufacturer != null && c.Model != null)
            .Select(c => new { c.Id, c.Name, c.Manufacturer, c.Model, c.FirmwareVersion, c.IsEnabled })
            .ToListAsync(ct);

        return candidates
            .Where(c => ModelKey(c.Manufacturer, c.Model) == key)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new DeviceSettingsPeerDto(c.Id, c.Name, c.FirmwareVersion,
                FirmwareDiffers: !string.Equals(c.FirmwareVersion?.Trim(), camera.FirmwareVersion?.Trim(), StringComparison.OrdinalIgnoreCase),
                c.IsEnabled))
            .ToList();
    }

    public async Task<IReadOnlyList<BulkDeviceSettingsCameraResult>> ApplyBulkAsync(BulkDeviceSettingsRequest request, CancellationToken ct = default)
    {
        var ids = request.CameraIds.Distinct().ToList();
        if (ids.Count == 0) throw new DeviceSettingsValidationException("Choose at least one camera.");
        if ((request.Video is null || request.Video.Count == 0) && request.Image is null && request.Audio is null)
            throw new DeviceSettingsValidationException("There's nothing to apply.");

        var cameras = await db.Cameras.AsNoTracking()
            .Include(c => c.Capabilities)
            .Include(c => c.Streams)
            .Where(c => ids.Contains(c.Id))
            .ToListAsync(ct);
        if (cameras.Count != ids.Count) throw new DeviceSettingsValidationException("One or more of the chosen cameras no longer exist.");

        var keys = cameras.Select(ModelKey).Distinct().ToList();
        if (keys.Count != 1 || keys[0] is null)
            throw new DeviceSettingsValidationException("Bulk changes only apply to cameras of the same make and model.");

        var results = new ConcurrentDictionary<Guid, (BulkDeviceSettingsCameraResult Result, bool Reprobe)>();
        await Parallel.ForEachAsync(cameras, new ParallelOptions { MaxDegreeOfParallelism = BulkParallelism, CancellationToken = ct },
            async (camera, token) => results[camera.Id] = await ApplyToCameraAsync(camera, request, token));

        // Sequential on purpose: ProbeAsync uses this request's DbContext.
        foreach (var (id, (_, reprobe)) in results)
        {
            if (!reprobe) continue;
            try { await cameraService.ProbeAsync(id, ct); }
            catch (Exception ex) when (IsCameraError(ex)) { /* the change itself landed; the daily re-probe catches up */ }
        }

        return ids.Select(id => results[id].Result).ToList();
    }

    private async Task<(BulkDeviceSettingsCameraResult, bool)> ApplyToCameraAsync(Camera camera, BulkDeviceSettingsRequest request, CancellationToken ct)
    {
        var messages = new List<string>();
        int applied = 0, skipped = 0, failed = 0;
        var reprobe = false;

        BulkDeviceSettingsCameraResult Result(BulkDeviceSettingsStatus status) => new(camera.Id, camera.Name, status, messages);

        Target target;
        try { target = ToTarget(camera); }
        catch (DeviceSettingsUnavailableException ex)
        {
            messages.Add(ex.Message);
            return (Result(BulkDeviceSettingsStatus.Skipped), false);
        }

        var soap = NewSoap();
        IReadOnlyList<OnvifMediaProfile> profiles;
        try { profiles = await new OnvifMediaClient(soap).GetProfilesAsync(target.Media, target.Credentials, ct); }
        catch (Exception ex) when (IsCameraError(ex))
        {
            messages.Add($"Couldn't reach the camera: {ex.Message}");
            return (Result(BulkDeviceSettingsStatus.Failed), false);
        }

        async Task Part(string what, Func<Task> apply, bool needsReprobe)
        {
            try
            {
                await apply();
                applied++;
                reprobe |= needsReprobe;
            }
            catch (Exception ex) when (ex is DeviceSettingsValidationException or DeviceSettingsUnavailableException)
            {
                skipped++;
                messages.Add($"{what}: {ex.Message}");
            }
            catch (Exception ex) when (IsCameraError(ex))
            {
                failed++;
                messages.Add($"{what}: {ex.Message}");
            }
        }

        foreach (var (role, change) in request.Video ?? [])
            await Part($"{role} stream", () => ApplyVideoAsync(soap, target, profiles, role, change, ct), needsReprobe: true);
        if (request.Image is { } image)
            await Part("Image", () => ApplyImageAsync(soap, target, profiles, image, ct), needsReprobe: false);
        if (request.Audio is { } audio)
        {
            await Part("Audio", () =>
            {
                var main = target.Streams.FirstOrDefault(s => s.Role == CameraStreamRole.Main);
                var token = main is null ? null : FindProfile(profiles, main.ProfileToken)?.AudioEncoderConfigurationToken;
                if (token is null) throw new DeviceSettingsUnavailableException("This camera has no audio encoder on its Main stream.");
                return ApplyAudioAsync(soap, target, profiles, token, audio, ct);
            }, needsReprobe: true);
        }

        var status = (applied, skipped, failed) switch
        {
            ( > 0, 0, 0) => BulkDeviceSettingsStatus.Applied,
            (0, _, 0) => BulkDeviceSettingsStatus.Skipped,
            (0, _, > 0) => BulkDeviceSettingsStatus.Failed,
            _ => BulkDeviceSettingsStatus.Partial,
        };
        return (Result(status), reprobe);
    }

    // ── Apply one part to one camera (no DbContext use — safe to run in parallel) ──────────────

    private async Task ApplyVideoAsync(OnvifSoapClient soap, Target target, IReadOnlyList<OnvifMediaProfile> profiles,
        string role, VideoEncoderValues change, CancellationToken ct)
    {
        if (!Enum.TryParse<CameraStreamRole>(role, ignoreCase: true, out var streamRole) || !Enum.IsDefined(streamRole))
            throw new DeviceSettingsValidationException($"Unknown stream \"{role}\".");
        var stream = target.Streams.FirstOrDefault(s => s.Role == streamRole)
            ?? throw new DeviceSettingsUnavailableException($"This camera has no {streamRole} stream.");
        var profile = FindProfile(profiles, stream.ProfileToken)
            ?? throw new DeviceSettingsUnavailableException($"The camera no longer reports the {stream.Label} stream. Re-probe it from its Edit page.");
        var configToken = profile.VideoEncoderConfigurationToken
            ?? throw new DeviceSettingsUnavailableException($"The camera doesn't report encoder settings for the {stream.Label} stream.");

        var state = await ReadEncoderAsync(soap, target, configToken, profile.Token, ct);
        var onvifChange = ValidateVideo(change, state.Config, state.Options, state.ViaMedia2);
        if (onvifChange == new OnvifVideoEncoderChange()) return;

        if (state.ViaMedia2)
            await new OnvifMedia2Client(soap).SetVideoEncoderConfigurationAsync(target.Media2!, state.Config, onvifChange, target.Credentials, ct);
        else
            await new OnvifMediaClient(soap).SetVideoEncoderConfigurationAsync(target.Media, state.Config, onvifChange, target.Credentials, ct);
    }

    private static async Task ApplyImageAsync(OnvifSoapClient soap, Target target, IReadOnlyList<OnvifMediaProfile> profiles,
        ImageSettingsValues change, CancellationToken ct)
    {
        var imagingUri = target.Imaging ?? throw new DeviceSettingsUnavailableException("This camera doesn't have adjustable image settings.");
        var sourceToken = SourceToken(target, profiles)
            ?? throw new DeviceSettingsUnavailableException("The camera doesn't report which video source to adjust.");

        var imaging = new OnvifImagingClient(soap);
        var settings = await imaging.GetImagingSettingsAsync(imagingUri, sourceToken, target.Credentials, ct);
        var options = await imaging.GetOptionsAsync(imagingUri, sourceToken, target.Credentials, ct);
        var onvifChange = ValidateImage(change, options);
        if (onvifChange == new OnvifImagingChange()) return;
        await imaging.SetImagingSettingsAsync(imagingUri, sourceToken, settings, onvifChange, target.Credentials, ct);
    }

    private static async Task ApplyAudioAsync(OnvifSoapClient soap, Target target, IReadOnlyList<OnvifMediaProfile> profiles,
        string configToken, AudioEncoderValues change, CancellationToken ct)
    {
        // Only configurations this camera's own streams use — never an arbitrary token from the request.
        var owner = target.Streams.FirstOrDefault(s => FindProfile(profiles, s.ProfileToken)?.AudioEncoderConfigurationToken == configToken)
            ?? throw new DeviceSettingsUnavailableException("This camera's streams don't use that audio encoder.");

        var media = new OnvifMediaClient(soap);
        var config = await media.GetAudioEncoderConfigurationAsync(target.Media, configToken, target.Credentials, ct);
        var options = await media.GetAudioEncoderConfigurationOptionsAsync(target.Media, configToken, owner.ProfileToken, target.Credentials, ct);
        var onvifChange = ValidateAudio(change, config, options);
        if (onvifChange == new OnvifAudioEncoderChange()) return;
        await media.SetAudioEncoderConfigurationAsync(target.Media, config, onvifChange, target.Credentials, ct);
    }

    // ── Validation (internal for tests) ─────────────────────────────────────────────────────────

    internal static OnvifVideoEncoderChange ValidateVideo(VideoEncoderValues change, OnvifVideoEncoderConfig current,
        IReadOnlyList<OnvifVideoEncodingOptions> options, bool viaMedia2)
    {
        var codec = change.Codec ?? current.Encoding;
        var opts = options.FirstOrDefault(o => string.Equals(o.Encoding, codec, StringComparison.OrdinalIgnoreCase));
        if (opts is null)
        {
            throw new DeviceSettingsValidationException(change.Codec is not null
                ? $"{CodecName(change.Codec)} isn't available on this stream."
                : "The camera didn't report which settings this stream accepts.");
        }
        var codecChanged = change.Codec is not null && !string.Equals(change.Codec, current.Encoding, StringComparison.OrdinalIgnoreCase);

        OnvifResolution? resolution = null;
        if (change.Width is not null || change.Height is not null)
        {
            if (change.Width is not { } w || change.Height is not { } h)
                throw new DeviceSettingsValidationException("Choose both a width and a height.");
            if (opts.Resolutions.Count == 0)
                throw new DeviceSettingsValidationException("Resolution isn't adjustable on this stream.");
            if (!opts.Resolutions.Contains(new OnvifResolution(w, h)))
                throw new DeviceSettingsValidationException($"{w}×{h} isn't available for {CodecName(opts.Encoding)}.");
            resolution = new OnvifResolution(w, h);
        }
        else if (codecChanged && current.Resolution is { } cr && opts.Resolutions.Count > 0 && !opts.Resolutions.Contains(cr))
        {
            throw new DeviceSettingsValidationException($"{cr.Width}×{cr.Height} isn't available for {CodecName(opts.Encoding)}. Choose a resolution too.");
        }

        if (change.FrameRate is { } fps)
        {
            if (fps <= 0) throw new DeviceSettingsValidationException("Frame rate must be above 0.");
            if (opts.FrameRatesSupported.Count > 0)
            {
                if (!opts.FrameRatesSupported.Any(f => Math.Abs(f - fps) < 0.01))
                    throw new DeviceSettingsValidationException($"{Num(fps)} fps isn't offered. Choose one of {JoinList(opts.FrameRatesSupported.Select(Num))}.");
            }
            else if (opts.FrameRateRange is { } fr)
            {
                InRange(fps, fr, "Frame rate");
            }
            else throw new DeviceSettingsValidationException("Frame rate isn't adjustable on this stream.");
        }

        if (change.BitrateKbps is { } kbps)
        {
            if (opts.BitrateRangeKbps is { } br) InRange(kbps, br, "Bitrate");
            else if (current.BitrateLimitKbps is null) throw new DeviceSettingsValidationException("Bitrate isn't adjustable on this stream.");
            else if (kbps is < 16 or > 100_000) throw new DeviceSettingsValidationException("Bitrate must be between 16 and 100000 kbps.");
        }

        bool? constantBitRate = null;
        if (change.RateControl is { } rc)
        {
            if (!viaMedia2 || !opts.ConstantBitRateSupported)
                throw new DeviceSettingsValidationException("This stream doesn't offer a choice between CBR and VBR.");
            constantBitRate = rc.ToUpperInvariant() switch
            {
                "CBR" => true,
                "VBR" => false,
                _ => throw new DeviceSettingsValidationException("Bitrate mode must be CBR or VBR."),
            };
        }

        if (change.GovLength is { } gov)
        {
            if (opts.Encoding == "JPEG") throw new DeviceSettingsValidationException("JPEG has no keyframe interval.");
            if (opts.GovLengthRange is { } gr) InRange(gov, gr, "Keyframe interval");
            else if (current.GovLength is null) throw new DeviceSettingsValidationException("Keyframe interval isn't adjustable on this stream.");
            else if (gov is < 1 or > 1000) throw new DeviceSettingsValidationException("Keyframe interval must be between 1 and 1000 frames.");
        }

        string? profileName = null;
        if (change.Profile is { } p)
        {
            profileName = opts.Profiles.FirstOrDefault(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase))
                ?? throw new DeviceSettingsValidationException(opts.Profiles.Count == 0
                    ? "Encoding profile isn't adjustable on this stream."
                    : $"\"{p}\" isn't available. Choose one of {JoinList(opts.Profiles)}.");
        }

        if (change.Quality is { } q)
        {
            if (opts.QualityRange is not { } qr) throw new DeviceSettingsValidationException("Quality isn't adjustable on this stream.");
            InRange(q, qr, "Quality");
        }

        return new OnvifVideoEncoderChange(
            Encoding: change.Codec is null ? null : opts.Encoding,
            Resolution: resolution,
            FrameRateLimit: change.FrameRate,
            BitrateLimitKbps: change.BitrateKbps,
            ConstantBitRate: constantBitRate,
            GovLength: change.GovLength,
            Profile: profileName,
            Quality: change.Quality);
    }

    internal static OnvifImagingChange ValidateImage(ImageSettingsValues change, OnvifImagingOptions options)
    {
        double? Level(double? value, OnvifRange? range, string label)
        {
            if (value is not { } v) return null;
            if (range is not { } r) throw new DeviceSettingsValidationException($"{label} isn't adjustable on this camera.");
            InRange(v, r, label);
            return v;
        }

        string? Mode(string? value, IReadOnlyList<string> modes, string label)
        {
            if (value is null) return null;
            if (modes.Count == 0) throw new DeviceSettingsValidationException($"{label} isn't adjustable on this camera.");
            return modes.FirstOrDefault(m => string.Equals(m, value, StringComparison.OrdinalIgnoreCase))
                ?? throw new DeviceSettingsValidationException($"\"{value}\" isn't available for {label.ToLowerInvariant()}. Choose one of {JoinList(modes)}.");
        }

        return new OnvifImagingChange(
            Brightness: Level(change.Brightness, options.Brightness, "Brightness"),
            ColorSaturation: Level(change.Saturation, options.ColorSaturation, "Saturation"),
            Contrast: Level(change.Contrast, options.Contrast, "Contrast"),
            Sharpness: Level(change.Sharpness, options.Sharpness, "Sharpness"),
            IrCutFilter: Mode(change.IrCutFilter, options.IrCutFilterModes, "Day/night mode"),
            WideDynamicRangeMode: Mode(change.WdrMode, options.WideDynamicRangeModes, "WDR"),
            WideDynamicRangeLevel: Level(change.WdrLevel, options.WideDynamicRangeLevel, "WDR level"),
            BacklightCompensationMode: Mode(change.BacklightMode, options.BacklightCompensationModes, "Backlight compensation"),
            BacklightCompensationLevel: Level(change.BacklightLevel, options.BacklightCompensationLevel, "Backlight compensation level"),
            ExposureMode: Mode(change.ExposureMode, options.ExposureModes, "Exposure mode"),
            WhiteBalanceMode: Mode(change.WhiteBalanceMode, options.WhiteBalanceModes, "White balance"),
            WhiteBalanceCrGain: Level(change.WhiteBalanceCrGain, options.WhiteBalanceCrGain, "Red gain"),
            WhiteBalanceCbGain: Level(change.WhiteBalanceCbGain, options.WhiteBalanceCbGain, "Blue gain"));
    }

    internal static OnvifAudioEncoderChange ValidateAudio(AudioEncoderValues change, OnvifAudioEncoderConfig current,
        IReadOnlyList<OnvifAudioEncodingOptions> options)
    {
        var codec = change.Codec ?? current.Encoding;
        var opts = options.FirstOrDefault(o => string.Equals(o.Encoding, codec, StringComparison.OrdinalIgnoreCase))
            ?? throw new DeviceSettingsValidationException(change.Codec is not null
                ? $"{change.Codec} audio isn't available on this camera."
                : "The camera didn't report which audio settings it accepts.");

        int? Pick(int? value, IReadOnlyList<int> allowed, string label, string unit)
        {
            if (value is not { } v) return null;
            if (allowed.Count == 0) throw new DeviceSettingsValidationException($"Audio {label} isn't adjustable for {opts.Encoding}.");
            if (!allowed.Contains(v))
                throw new DeviceSettingsValidationException($"{v} {unit} isn't available. Choose one of {JoinList(allowed.Select(a => $"{a} {unit}"))}.");
            return v;
        }

        return new OnvifAudioEncoderChange(
            Encoding: change.Codec is null ? null : opts.Encoding,
            BitrateKbps: Pick(change.BitrateKbps, opts.BitratesKbps, "bitrate", "kbps"),
            SampleRateKhz: Pick(change.SampleRateKhz, opts.SampleRatesKhz, "sample rate", "kHz"));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private OnvifSoapClient NewSoap() => new(httpClientFactory());

    private Task<Camera?> LoadCameraAsync(Guid cameraId, CancellationToken ct)
        => db.Cameras.AsNoTracking()
            .Include(c => c.Capabilities)
            .Include(c => c.Streams)
            .FirstOrDefaultAsync(c => c.Id == cameraId, ct);

    private async Task<Target> LoadTargetAsync(Guid cameraId, CancellationToken ct)
        => ToTarget(await LoadCameraAsync(cameraId, ct) ?? throw new KeyNotFoundException("Camera not found."));

    internal static Target ToTarget(Camera camera)
    {
        if (string.IsNullOrWhiteSpace(camera.DeviceServiceUri))
            throw new DeviceSettingsUnavailableException("This camera has no ONVIF device address.");
        var raw = camera.Capabilities?.RawProbeJson;
        var media = OnvifServiceAddresses.Get(raw, "Media")
            ?? throw new DeviceSettingsUnavailableException("This camera hasn't been probed over ONVIF yet. Re-probe it from its Edit page.");

        var streams = camera.Streams
            .Where(s => !string.IsNullOrEmpty(s.ProfileToken))
            .OrderBy(s => s.Role)
            .Select(s => new StreamRef(s.Role, s.ProfileToken, string.IsNullOrWhiteSpace(s.CustomName) ? s.Role.ToString() : s.CustomName!))
            .ToList();

        return new Target(
            camera.Id, camera.Name, OnvifServiceAddresses.Credentials(camera.Username, camera.Password),
            media,
            camera.Capabilities?.HasMedia2 == true ? OnvifServiceAddresses.Get(raw, "Media2") : null,
            camera.Capabilities?.HasImaging == true ? OnvifServiceAddresses.Get(raw, "Imaging") : null,
            camera.VideoSourceToken,
            streams);
    }

    private static OnvifMediaProfile? FindProfile(IReadOnlyList<OnvifMediaProfile> profiles, string token)
        => profiles.FirstOrDefault(p => p.Token == token);

    /// <summary>The camera's pinned sensor on a multi-sensor device; otherwise the Main stream's own
    /// video source, falling back to whichever source any profile reports.</summary>
    private static string? SourceToken(Target target, IReadOnlyList<OnvifMediaProfile> profiles)
        => target.VideoSourceToken
           ?? target.Streams.Select(s => FindProfile(profiles, s.ProfileToken)?.VideoSourceToken).FirstOrDefault(t => t is not null)
           ?? profiles.Select(p => p.VideoSourceToken).FirstOrDefault(t => t is not null);

    private async Task<EncoderState> ReadEncoderAsync(OnvifSoapClient soap, Target target, string configToken, string profileToken, CancellationToken ct)
    {
        if (target.Media2 is { } media2Uri)
        {
            try
            {
                var media2 = new OnvifMedia2Client(soap);
                var config = await media2.GetVideoEncoderConfigurationAsync(media2Uri, configToken, target.Credentials, ct);
                if (config is not null)
                {
                    // No profile token: Media2 profile tokens aren't guaranteed to match Media1's.
                    var options = await media2.GetVideoEncoderConfigurationOptionsAsync(media2Uri, configToken, null, target.Credentials, ct);
                    if (options.Count > 0) return new EncoderState(config, options, ViaMedia2: true);
                }
            }
            catch (OnvifFaultException) { /* fall back to Media1 below */ }
        }

        var media = new OnvifMediaClient(soap);
        var m1Config = await media.GetVideoEncoderConfigurationAsync(target.Media, configToken, target.Credentials, ct);
        var m1Options = await media.GetVideoEncoderConfigurationOptionsAsync(target.Media, configToken, profileToken, target.Credentials, ct);
        return new EncoderState(m1Config, m1Options, ViaMedia2: false);
    }

    private static string? ModelKey(Camera c) => ModelKey(c.Manufacturer, c.Model);

    private static string? ModelKey(string? manufacturer, string? model)
        => string.IsNullOrWhiteSpace(manufacturer) || string.IsNullOrWhiteSpace(model)
            ? null
            : $"{manufacturer.Trim().ToUpperInvariant()}\u001f{model.Trim().ToUpperInvariant()}";

    private static bool IsCameraError(Exception ex)
        => ex is OnvifFaultException or HttpRequestException or System.Xml.XmlException
           || (ex is TaskCanceledException tce && tce.InnerException is TimeoutException);

    private static VideoEncoderValues ToValues(OnvifVideoEncoderConfig c) => new(
        c.Encoding, c.Profile, c.Resolution?.Width, c.Resolution?.Height, c.FrameRateLimit, c.BitrateLimitKbps,
        c.ConstantBitRate switch { true => "CBR", false => "VBR", null => null }, c.GovLength, c.Quality);

    private static VideoCodecOptionsDto ToDto(OnvifVideoEncodingOptions o, bool viaMedia2) => new(
        o.Encoding,
        o.Resolutions.OrderByDescending(r => r.Width * r.Height).Select(r => new DeviceSettingsResolution(r.Width, r.Height)).ToList(),
        o.FrameRatesSupported,
        ToDto(o.FrameRateRange),
        ToDto(o.BitrateRangeKbps),
        ToDto(o.GovLengthRange),
        ToDto(o.QualityRange),
        o.Profiles,
        RateControlSupported: viaMedia2 && o.ConstantBitRateSupported);

    private static ImageSettingsValues ToValues(OnvifImagingSettings s) => new(
        s.Brightness, s.ColorSaturation, s.Contrast, s.Sharpness, s.IrCutFilter,
        s.WideDynamicRangeMode, s.WideDynamicRangeLevel, s.BacklightCompensationMode, s.BacklightCompensationLevel,
        s.ExposureMode, s.WhiteBalanceMode, s.WhiteBalanceCrGain, s.WhiteBalanceCbGain);

    private static ImageSettingsOptionsDto ToDto(OnvifImagingOptions o) => new(
        ToDto(o.Brightness), ToDto(o.ColorSaturation), ToDto(o.Contrast), ToDto(o.Sharpness),
        o.IrCutFilterModes, o.WideDynamicRangeModes, ToDto(o.WideDynamicRangeLevel),
        o.BacklightCompensationModes, ToDto(o.BacklightCompensationLevel), o.ExposureModes,
        o.WhiteBalanceModes, ToDto(o.WhiteBalanceCrGain), ToDto(o.WhiteBalanceCbGain));

    private static DeviceSettingsRange? ToDto(OnvifRange? r) => r is null ? null : new DeviceSettingsRange(r.Min, r.Max);

    private static bool HasAnyImageOption(ImageSettingsOptionsDto o)
        => o.Brightness is not null || o.Saturation is not null || o.Contrast is not null || o.Sharpness is not null
           || o.IrCutFilterModes.Count > 0 || o.WdrModes.Count > 0 || o.BacklightModes.Count > 0
           || o.ExposureModes.Count > 0 || o.WhiteBalanceModes.Count > 0;

    private static void InRange(double value, OnvifRange range, string label)
    {
        // A small tolerance: devices report float ranges (e.g. 0–100) that the UI rounds for display.
        if (value < range.Min - 0.001 || value > range.Max + 0.001)
            throw new DeviceSettingsValidationException($"{label} must be between {Num(range.Min)} and {Num(range.Max)}.");
    }

    private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string CodecName(string codec) => codec.ToUpperInvariant() switch
    {
        "H264" => "H.264",
        "H265" => "H.265",
        "MPEG4" => "MPEG-4",
        _ => codec,
    };

    /// <summary>"a", "a or b", "a, b, or c" — serial comma.</summary>
    private static string JoinList(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            2 => $"{list[0]} or {list[1]}",
            _ => $"{string.Join(", ", list.Take(list.Count - 1))}, or {list[^1]}",
        };
    }
}
