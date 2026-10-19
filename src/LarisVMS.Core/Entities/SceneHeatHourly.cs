namespace LarisVMS.Core.Entities;

/// <summary>What a scene heatmap counts.</summary>
public enum SceneHeatSource
{
    /// <summary>Paths of moving tracked objects: each object counts once in every cell it passed through.</summary>
    Objects = 0,

    /// <summary>The node's grid motion detection: analysed frames with motion in each cell.</summary>
    Motion = 1,
}

/// <summary>
/// One camera's scene heat for one UTC hour: a grid of counts laid over the camera's picture, stored
/// as only its non-zero cells (SceneHeat.EncodeCells). Objects rows are built hourly by
/// SceneHeatRollupService from DetectedObject paths, one per object category; Motion rows are
/// reported by the node every few minutes, one per node run (RunId), and added together when read.
/// Kept as long as the activity history (Activity.HistoryDays), like ActivityHourlyCount.
/// </summary>
public class SceneHeatHourly
{
    public long Id { get; set; }
    public Guid CameraId { get; set; }
    public DateTime HourUtc { get; set; }
    public SceneHeatSource Source { get; set; }

    /// <summary>Objects: the category (Human, Vehicle, Animal, Object). Motion: "".</summary>
    public string Label { get; set; } = "";

    public int Cols { get; set; }
    public int Rows { get; set; }
    public byte[] Cells { get; set; } = [];

    /// <summary>Motion only: the node run that reported these counts. A run reports its running totals
    /// for the hour, which replace its own earlier report, so a retried post can't count twice.</summary>
    public Guid? RunId { get; set; }
}
