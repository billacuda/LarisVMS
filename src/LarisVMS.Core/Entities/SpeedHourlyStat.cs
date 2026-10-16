namespace LarisVMS.Core.Entities;

/// <summary>
/// One camera's speed-trap measurements for one UTC hour, per trap and object label — the speed
/// heatmap's long-term history. Built by SpeedRollupService from DetectedObjects (which go away with
/// footage retention) and kept as long as the activity history (Activity.HistoryDays). Its own table
/// rather than rows in ActivityHourlyCount: it carries a sum and a maximum, not just a count, and it
/// must never show up in the event heatmap.
/// </summary>
public class SpeedHourlyStat
{
    public long Id { get; set; }
    public Guid CameraId { get; set; }

    /// <summary>The trap that measured these. Plain column, no foreign key: a deleted trap's history
    /// stays and shows as "(deleted trap)".</summary>
    public Guid? SpeedTrapId { get; set; }

    public DateTime HourUtc { get; set; }

    /// <summary>The objects' label (e.g. "car").</summary>
    public string? Label { get; set; }

    public int Count { get; set; }
    public double SumKph { get; set; }
    public double MaxKph { get; set; }

    /// <summary>How many beat the trap's speed limit at the time they were rolled up.</summary>
    public int OverLimitCount { get; set; }
}
