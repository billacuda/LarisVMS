namespace NidusVMS.Core;

/// <summary>
/// M8 pass 6: decides whether an ONVIF PullPoint notification represents motion becoming active —
/// the only classification a camera-pushed event needs to drive the same MotionSpans/gating pipeline
/// ServerMotion zones already do. Pure and stateless so it's directly unit-testable and usable from
/// both NidusVMS.Node (classifies as each notification arrives, to feed MotionHysteresis) and
/// anywhere server-side that might want to re-classify a stored CameraEvent later.
/// </summary>
public static class CameraEventClassifier
{
    // ONVIF's own topic namespace for motion-family analytics/alarm events — vendor firmwares
    // differ in which of these they actually emit (confirmed: not every device implements every
    // topic), so this matches by substring within the well-known "tns1:..." family rather than
    // requiring an exact string.
    private static readonly string[] MotionTopicMarkers =
        ["MotionDetector", "MotionAlarm", "MotionRegionDetector", "CellMotionDetector"];

    /// <summary>True only when the topic is a recognized motion-family event AND its own boolean
    /// state item says motion is presently active — every motion-family topic ONVIF defines fires
    /// on both the rising and falling edge (motion starting AND motion clearing use the same topic),
    /// so the topic alone is never enough. The state item's name varies by topic ("State" for
    /// RuleEngine detectors, "IsMotion" for VideoSource/MotionAlarm) — checks whichever is present.
    /// Missing or unparseable state is treated as "not active" rather than "active": never manufacture
    /// motion from a payload that doesn't actually confirm it.</summary>
    public static bool IsMotionActive(string? topic, IReadOnlyDictionary<string, string> simpleItems)
    {
        if (string.IsNullOrEmpty(topic)) return false;
        if (!MotionTopicMarkers.Any(m => topic.Contains(m, StringComparison.OrdinalIgnoreCase))) return false;

        return TryGetBooleanState(simpleItems);
    }

    /// <summary>M8 pass 8: the same "find whichever boolean state item is present" lookup
    /// IsMotionActive uses for the built-in motion-family topics, generalized for a user-configured
    /// EventTagRule's single-topic toggle mode — a rule with no StopTopic re-derives open/closed from
    /// this same item on every notification for its StartTopic, rather than requiring a distinct
    /// topic for the falling edge. Same "missing/unparseable state means not active" reasoning as
    /// IsMotionActive: never manufacture an edge from a payload that doesn't actually confirm one.</summary>
    public static bool TryGetBooleanState(IReadOnlyDictionary<string, string> simpleItems)
    {
        foreach (var name in new[] { "State", "IsMotion", "Motion" })
        {
            if (simpleItems.TryGetValue(name, out var raw) && bool.TryParse(raw, out var value))
                return value;
        }
        return false;
    }
}
