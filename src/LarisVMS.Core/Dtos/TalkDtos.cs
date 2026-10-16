namespace LarisVMS.Core.Dtos;

/// <summary>One recorded push-to-talk press, as the Playback page sees it — played from
/// GET /talk-audio/{cameraId}/{id} in sync with the video.</summary>
public record TalkRecordingDto(long Id, Guid CameraId, DateTime StartUtc, DateTime EndUtc, string? UserName);
