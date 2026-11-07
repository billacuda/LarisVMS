using LarisVMS.Core.Dtos;

namespace LarisVMS.Core.Interfaces;

/// <summary>Starts and stops manual recordings. Both are idempotent, so repeating a call is safe.</summary>
public interface IManualRecordingService
{
    Task<ManualRecordingChangeResult> StartAsync(IReadOnlyCollection<Guid> cameraIds, string? actor, CancellationToken ct = default);

    Task<ManualRecordingChangeResult> StopAsync(IReadOnlyCollection<Guid> cameraIds, string? actor, CancellationToken ct = default);

    /// <summary>Every camera keeping footage or with a manual recording on. See <see cref="CameraRecordingStateDto"/>.</summary>
    Task<List<CameraRecordingStateDto>> GetStatesAsync(CancellationToken ct = default);
}
