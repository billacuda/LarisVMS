namespace LarisVMS.Core.Entities;

/// <summary>Where an activity count came from. The rollup already applies the "AI detection wins over
/// the camera's own classes" rule per camera per hour, so this is kept for diagnostics and future
/// filtering rather than needed by the heatmap query.</summary>
public enum ActivitySource : byte { Ai = 0, Camera = 1, Tag = 2, Motion = 3 }

/// <summary>How many events of one type/label started on one camera in one UTC hour — the activity
/// heatmaps' long-term history. Built from MotionSpan rows by ActivityRollupService once each hour is
/// finished, and kept for Activity.HistoryDays (default 400) regardless of footage retention: MotionSpan
/// rows are deleted as soon as the footage they point at ages out, which would otherwise cap the 90-day
/// and 1-year heatmaps at however much footage a camera keeps. A finished hour is built once and only
/// ever re-read, which is also what keeps those long ranges quick to load.</summary>
public class ActivityHourlyCount
{
    public long Id { get; set; }
    public Guid CameraId { get; set; }
    public DateTime HourUtc { get; set; }
    public ActivitySource Source { get; set; }

    /// <summary>"Human", "Vehicle", "Animal", "Object", any other AI category, "Custom tags" or "Motion".</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>"cat", "car", a tag rule's name, a camera class — null for plain motion.</summary>
    public string? Label { get; set; }

    public int Count { get; set; }
}
