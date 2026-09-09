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

    /// <summary>Required when ConditionType is CameraNotReporting, otherwise null.</summary>
    public Guid? CameraId { get; set; }

    /// <summary>Required when ConditionType is NodeOffline, NodeStorageLow or NodeFailoverActivated,
    /// otherwise null.</summary>
    public Guid? NodeId { get; set; }

    /// <summary>Required when ConditionType is NodeStorageLow — fires when free space drops below
    /// this percentage. Unused for the other condition types.</summary>
    public int? ThresholdPercent { get; set; }

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
