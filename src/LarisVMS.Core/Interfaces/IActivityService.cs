using LarisVMS.Core.Dtos;

namespace LarisVMS.Core.Interfaces;

/// <summary>Activity heatmaps — when, by time of day, each type of event happens.</summary>
public interface IActivityService
{
    /// <summary>One camera's heatmap, or all cameras combined when cameraId is null. Accessible is the
    /// viewer's CameraAccess Playback set (null = unrestricted): a camera outside it returns null, and
    /// the combined heatmap only counts cameras inside it.
    ///
    /// Range is "24h", "7d", "30d", "90d" or "1y". View is "pattern" (day of week × hour, the default)
    /// or "calendar" (each day × hour; 30 days and up only). Selection holds event-type keys ("Animal")
    /// and label keys ("Animal:cat"); null or empty means the default (everything but plain motion).</summary>
    Task<ActivityHeatmapDto?> GetHeatmapAsync(Guid? cameraId, IReadOnlySet<Guid>? accessible, string? range, string? view,
        IReadOnlyList<string>? selection, CancellationToken ct = default);

    /// <summary>The speed heatmap: speed-trap measurements for one camera (or every accessible
    /// camera when cameraId is null), folded the same way as the event heatmap. Metric is "avg",
    /// "max", "over" or "count"; trapIds narrows to those traps (empty = all). Null when the camera
    /// doesn't exist or isn't accessible.</summary>
    Task<SpeedHeatmapDto?> GetSpeedHeatmapAsync(Guid? cameraId, IReadOnlySet<Guid>? accessible, string? range, string? view,
        string? metric, IReadOnlyList<string>? trapIds, CancellationToken ct = default);

    /// <summary>The scene heatmap: where in the picture objects moved (source "objects"; categories
    /// narrows to Human/Vehicle/Animal/Object, empty = all) or where grid motion happened (source
    /// "motion") over the range, optionally only between <paramref name="fromHour"/> and
    /// <paramref name="toHour"/> in the camera's local time (wrapping past midnight). Null when the
    /// camera doesn't exist or isn't accessible.</summary>
    Task<SceneHeatDto?> GetSceneHeatAsync(Guid cameraId, IReadOnlySet<Guid>? accessible, string? range, string? source,
        IReadOnlyList<string>? categories, int? fromHour, int? toHour, CancellationToken ct = default);
}
