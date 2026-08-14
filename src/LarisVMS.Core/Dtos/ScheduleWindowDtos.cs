namespace LarisVMS.Core.Dtos;

/// <summary>Days is the [Flags] DayOfWeekFlags' own ToString() (e.g. "Monday, Wednesday, Friday" or
/// "Weekdays") — parsed back with Enum.Parse&lt;DayOfWeekFlags&gt;, which supports comma-separated
/// flag names natively. Same string-not-raw-enum convention SaveZoneRequest's Kind uses.</summary>
public record SaveScheduleWindowRequest(string Days, TimeOnly StartTime, TimeOnly EndTime, bool IsEnabled = true);
