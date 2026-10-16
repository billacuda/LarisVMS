namespace LarisVMS.Core.Dtos;

/// <summary>An activity heatmap — when things happen on a camera (or all cameras), by time of day.
///
/// Mode "week" (ranges of 7 days or more, the "Typical week" view): Rows are days of the week, Columns
/// the 24 hours, each cell the events that started in that day/hour across the whole range;
/// WeekdayOccurrences says how many of each weekday the range covers, for per-day averages.
/// Mode "calendar" (30 days and up, the "By day" view): Rows are the 24 hours, Columns are the dates
/// (ISO yyyy-MM-dd, local), DayTotals each date's total.
/// Mode "day" (the last 24 hours): Columns are the 24 hours leading up to now, Rows the event types.
/// Times are local to the camera (the server's zone for all cameras).
///
/// Groups lists every event type seen in the range (with its labels, e.g. Animal → cat, dog) and its
/// total, whether or not it's selected — the page's filter tree is built from it. Peak and BusiestDay
/// describe the busiest times in words, for the summary line and for anyone not reading the colors.</summary>
public record ActivityHeatmapDto(Guid CameraId, string CameraName, string TimeZoneName, string Range, string Mode,
    List<string> RowLabels, List<string> ColumnLabels, List<List<int>> Cells, int Max, int Total,
    List<ActivityGroupDto> Groups, List<string> Selection, string? Peak,
    string? BusiestDay = null, List<int>? DayTotals = null, List<int>? WeekdayOccurrences = null);

public record ActivityGroupDto(string Key, string Name, string? Emoji, int Count, List<ActivityLabelDto> Labels);

/// <summary>Key is "Group:Label", the token the selection uses.</summary>
public record ActivityLabelDto(string Key, string Name, int Count);

/// <summary>The query's raw output: events of one type and label starting in one UTC hour.</summary>
/// <summary>The speed heatmap (Activity page, Speeds): same grid shapes as ActivityHeatmapDto —
/// "week", "calendar" and "day" modes — but each cell is a value for the chosen Metric ("avg" and
/// "max" in the display Unit, "over" and "count" as numbers of objects), null where nothing was
/// measured. Min/Max span the non-empty cells (the color scale). Limit is the speed limit in the
/// display unit when every selected trap has the same one, so the page can mark cells over it.
/// Traps lists the traps on offer; Selection is the trap ids applied (empty = all).</summary>
public record SpeedHeatmapDto(Guid CameraId, string CameraName, string TimeZoneName, string Range, string Mode,
    string Metric, string Unit, List<string> RowLabels, List<string> ColumnLabels, List<List<double?>> Cells,
    double Min, double Max, int Measured, double? Average, double? Top,
    List<SpeedTrapOptionDto> Traps, List<string> Selection, string? Peak, double? Limit,
    List<double?>? DayValues = null);

public record SpeedTrapOptionDto(string Id, string Name, int Count);

/// <summary>One hour of speed measurements as the speed heatmap builder takes them.</summary>
public record SpeedHourStat(DateTime HourUtc, Guid? TrapId, string? Label, int Count, double SumKph, double MaxKph, int OverLimit);

public record ActivityHourCount(DateTime HourUtc, string Group, string? Label, int Count);
