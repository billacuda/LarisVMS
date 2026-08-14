using LarisVMS.Core.Enums;

namespace LarisVMS.Core.Entities;

/// <summary>
/// One recurring time-of-day window for a camera in Recording.Mode=Schedule — a camera keeps
/// segments that start inside any of its enabled windows, the same "no config = fail open, keep
/// everything, warn" philosophy Motion mode uses for a camera with no zone (see
/// NodeWorker.IsWithinSchedule). Several windows per camera are expected (e.g. weekday business
/// hours + different weekend hours), each its own row rather than a single JSON blob, matching how
/// Zone/EventTagRule are each their own per-camera row instead of one settings value.
/// </summary>
public class ScheduleWindow
{
    public Guid Id { get; set; }
    public Guid CameraId { get; set; }

    /// <summary>Which day(s) this window applies to. A window that crosses midnight (EndTime &lt;
    /// StartTime) is considered to "belong" to the day it starts on — see NodeWorker.IsWithinSchedule
    /// for how a segment in the early-morning tail of such a window is matched against *that*
    /// starting day, not the calendar day the segment's own timestamp falls on.</summary>
    public DayOfWeekFlags Days { get; set; } = DayOfWeekFlags.All;

    public TimeOnly StartTime { get; set; }

    /// <summary>EndTime &lt; StartTime means the window crosses midnight (e.g. 22:00-02:00) — not an
    /// error, a deliberately supported case (see Days' own doc comment).</summary>
    public TimeOnly EndTime { get; set; }

    public bool IsEnabled { get; set; } = true;

    public Camera Camera { get; set; } = null!;
}
