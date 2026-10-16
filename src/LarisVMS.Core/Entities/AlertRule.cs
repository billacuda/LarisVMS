using LarisVMS.Core.Enums;

namespace LarisVMS.Core.Entities;

/// <summary>
/// One "watch this camera/node for this condition, and fire through these deliveries" rule, evaluated
/// by AlertEvaluatorService. Deliberately scoped to a single CameraId or NodeId rather than a
/// wildcard "every camera" rule — cooldown tracking (LastFiredAt) is then a single column on the rule
/// itself instead of needing a per-target row, and the admin UI stays a simple picker instead of a
/// fan-out preview. Watching a fleet of cameras means one rule per camera for now; folding "any
/// camera matching X" into one rule is a possible future refinement, not silently pretended away.
/// </summary>
public class AlertRule
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public AlertConditionType ConditionType { get; set; }

    /// <summary>Required when ConditionType is CameraNotReporting. Optional for ObjectDetected and
    /// SpeedOver — null watches every camera (one cooldown for the whole rule). Otherwise null.</summary>
    public Guid? CameraId { get; set; }

    /// <summary>Required when ConditionType is NodeOffline, NodeStorageLow or NodeFailoverActivated,
    /// otherwise null.</summary>
    public Guid? NodeId { get; set; }

    /// <summary>Required when ConditionType is NodeStorageLow — fires when free space drops below
    /// this percentage. Unused for the other condition types.</summary>
    public int? ThresholdPercent { get; set; }

    // ── Detection filters (ObjectDetected / SpeedOver only; all optional, null = any) ──

    /// <summary>AI category name, e.g. "Vehicle".</summary>
    public string? CategoryFilter { get; set; }

    /// <summary>Specific label within the category, e.g. "truck".</summary>
    public string? LabelFilter { get; set; }

    /// <summary>An ObjectColors name. Matches an object whose primary, shirt or pants color is
    /// this color.</summary>
    public string? ColorFilter { get; set; }

    /// <summary>Only objects whose box (bottom centre, where it meets the ground) is inside this
    /// zone of the rule's camera. Needs CameraId. Plain column, no foreign key: a deleted zone
    /// just stops matching.</summary>
    public Guid? ZoneId { get; set; }

    /// <summary>Required for SpeedOver: fires at or above this speed (km/h).</summary>
    public double? SpeedThresholdKph { get; set; }

    /// <summary>Days the rule is active. None/null = every day.</summary>
    public DayOfWeekFlags? ScheduleDays { get; set; }

    /// <summary>Active time-of-day window, in the web server's local time. Both null = all day.
    /// End before start crosses midnight (22:00-06:00), the same as ScheduleWindow.</summary>
    public TimeOnly? ScheduleStart { get; set; }
    public TimeOnly? ScheduleEnd { get; set; }

    /// <summary>Minimum time between firings of this rule, so a condition that stays true doesn't
    /// re-alert every evaluator tick. Deliberately no "resolved" notification when the condition
    /// clears — this pass only fires on trip, not on recovery.</summary>
    public int CooldownMinutes { get; set; } = 60;

    /// <summary>Null until this rule has fired at least once. Stamped by AlertEvaluatorService before
    /// dispatching deliveries, not after — a delivery that throws must not make the rule re-fire on
    /// the very next tick.</summary>
    public DateTime? LastFiredAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastModifiedAt { get; set; }
    public string? LastModifiedBy { get; set; }

    public List<AlertDelivery> Deliveries { get; set; } = [];
}
