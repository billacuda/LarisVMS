using LarisVMS.Core.Dtos;

namespace LarisVMS.Core.Interfaces;

/// <summary>
/// Reads and changes a camera's own configuration over ONVIF — stream encoders (Media2 where the
/// camera has it, Media1 otherwise), imaging, and audio encoders — so users don't have to sign in to
/// each camera's web page. Called straight from LarisVMS.Web, like IPtzService: each call is a quick
/// request/response to the camera.
///
/// Every read is live from the camera, never cached. Every change is validated against the options
/// the camera reports right then, and throws <see cref="DeviceSettingsValidationException"/> for a
/// value it wouldn't accept. Network and SOAP failures surface as the ONVIF client's own exceptions.
/// </summary>
public interface ICameraDeviceSettingsService
{
    /// <summary>Null when the camera doesn't exist.</summary>
    Task<CameraDeviceSettingsDto?> GetAsync(Guid cameraId, CancellationToken ct = default);

    /// <summary><paramref name="role"/> is the stream role (Main/Sub/Third).</summary>
    Task UpdateVideoAsync(Guid cameraId, string role, VideoEncoderValues change, CancellationToken ct = default);

    Task UpdateImageAsync(Guid cameraId, ImageSettingsValues change, CancellationToken ct = default);

    Task UpdateAudioAsync(Guid cameraId, string configToken, AudioEncoderValues change, CancellationToken ct = default);

    /// <summary>Other cameras with the same make and model as <paramref name="cameraId"/> (case-
    /// insensitive), excluding cameras that have no make or model reported.</summary>
    Task<IReadOnlyList<DeviceSettingsPeerDto>> GetSameModelPeersAsync(Guid cameraId, CancellationToken ct = default);

    /// <summary>Applies one change to every camera in the request, at most a few at a time. Throws
    /// <see cref="DeviceSettingsValidationException"/> when the cameras aren't all the same make and
    /// model. Otherwise never throws for a single camera's failure: each camera gets its own result.</summary>
    Task<IReadOnlyList<BulkDeviceSettingsCameraResult>> ApplyBulkAsync(BulkDeviceSettingsRequest request, CancellationToken ct = default);
}
