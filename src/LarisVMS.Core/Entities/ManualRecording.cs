namespace LarisVMS.Core.Entities;

/// <summary>
/// One operator- or API-started recording on a camera: footage overlapping it is kept whatever the
/// camera's recording mode would otherwise do. The row is the state, so a recording survives web and
/// node restarts and upgrades until someone stops it. EndUtc is null while it's running.
///
/// There's deliberately no unique "one active row per camera" index (filtered indexes are SQL Server
/// only). Start returns an existing active row, stop closes every open row for the camera, and
/// readers tolerate duplicates, so a double-click race is harmless.
/// </summary>
public class ManualRecording
{
    public long Id { get; set; }
    public Guid CameraId { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }

    /// <summary>Display name of who started it: a user name, or "API key: {name}".</summary>
    public string? StartedBy { get; set; }
    public string? EndedBy { get; set; }

    public Camera Camera { get; set; } = null!;
}
