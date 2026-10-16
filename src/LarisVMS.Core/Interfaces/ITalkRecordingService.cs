using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;

namespace LarisVMS.Core.Interfaces;

/// <summary>Recorded two-way audio presses (see TalkRecording).</summary>
public interface ITalkRecordingService
{
    Task AddAsync(TalkRecording recording, CancellationToken ct = default);

    /// <summary>This camera's presses overlapping [fromUtc, toUtc), oldest first.</summary>
    Task<List<TalkRecordingDto>> ListAsync(Guid cameraId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    Task<TalkRecording?> GetAsync(long id, CancellationToken ct = default);
}
