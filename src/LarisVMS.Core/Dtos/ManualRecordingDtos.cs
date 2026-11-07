namespace LarisVMS.Core.Dtos;

/// <summary>Body of the batch start and stop routes.</summary>
public record ManualRecordingBatchRequest(IReadOnlyList<Guid> CameraIds);

/// <summary>What a start or stop did. Changed cameras switched state, Unchanged ones were already in
/// the requested state, and Skipped ones can't record (disabled or unknown).</summary>
public record ManualRecordingChangeResult(IReadOnlyList<Guid> Changed, IReadOnlyList<Guid> Unchanged, IReadOnlyList<Guid> Skipped);

/// <summary>One camera's live recording state. Keeping means footage is being kept right now, for any
/// reason. Manual means a manual recording is on, even if the camera is offline and nothing is being
/// saved. Cameras with neither aren't listed.</summary>
public record CameraRecordingStateDto(Guid CameraId, bool Keeping, bool Manual, DateTime? ManualStartedUtc, string? ManualStartedBy);
