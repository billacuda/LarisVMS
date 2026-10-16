namespace LarisVMS.Core.Entities;

/// <summary>One push-to-talk press: what an operator said through a camera's speaker, recorded by the
/// node as a WAV (8 kHz mono PCM) next to the camera's footage. The camera's own microphone is in
/// the video recording already; this is the other direction. Written by LarisVMS.Web's /talk relay
/// when the node reports the finished clip, played in sync on the Playback timeline, and removed by
/// MotionSpanRetentionService once the footage around it has aged out (the node deletes the file by
/// the same retention).</summary>
public class TalkRecording
{
    public long Id { get; set; }
    public Guid CameraId { get; set; }

    /// <summary>The node holding the file — where /talk-audio is fetched from.</summary>
    public Guid NodeId { get; set; }

    /// <summary>Who talked. Kept as text as well as the id, so the name survives the user being deleted.</summary>
    public string? UserId { get; set; }
    public string? UserName { get; set; }

    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }

    /// <summary>Full path on the node (under cam-{id}/talk/).</summary>
    public string FilePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }

    public Camera Camera { get; set; } = null!;
}
