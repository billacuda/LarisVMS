namespace LarisVMS.Core.Enums;

/// <summary>Recording.Mode's four values (see ISettingsResolver). Stored as this enum's ToString()
/// in the setting's raw string value — the string wire format (Cameras/Edit's dropdown, the
/// NodeConfigCameraDto.RecordingMode string, Admin/Settings) is unchanged; NodeWorker is the one
/// place that parses it back into this enum, since it's the one place doing real branching logic
/// across four modes rather than just passing the value through.</summary>
public enum RecordingMode
{
    Continuous = 0,
    Motion = 1,
    Schedule = 2,
    Event = 3
}

/// <summary>Days a ScheduleWindow applies to. [Flags] so one window can span several days (e.g.
/// weekday business hours) without needing several rows — matches the plan doc's original mention
/// of "schedule bitmap evaluation" as a planned unit-test target. Sunday=1 through Saturday=64,
/// i.e. <c>1 &lt;&lt; (int)DayOfWeek.X</c>, so converting a BCL DayOfWeek is a single shift
/// (see NodeWorker.ToDayFlag) rather than a lookup table.</summary>
[Flags]
public enum DayOfWeekFlags
{
    None = 0,
    Sunday = 1,
    Monday = 2,
    Tuesday = 4,
    Wednesday = 8,
    Thursday = 16,
    Friday = 32,
    Saturday = 64,
    All = Sunday | Monday | Tuesday | Wednesday | Thursday | Friday | Saturday,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    Weekends = Saturday | Sunday
}
