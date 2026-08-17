using LarisVMS.Core.Enums;

namespace LarisVMS.Core;

/// <summary>
/// M8 pass 6: decides whether an ONVIF PullPoint notification represents motion becoming active —
/// the only classification a camera-pushed event needs to drive the same MotionSpans/gating pipeline
/// ServerMotion zones already do. Pure and stateless so it's directly unit-testable and usable from
/// both LarisVMS.Node (classifies as each notification arrives, to feed MotionHysteresis) and
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

    /// Object-detection topic markers, longest/most specific first so "FaceDetector" is never
    /// swallowed by a broader match. Matched by substring within the vendor's own topic string for
    /// the same reason MotionTopicMarkers is: there is no single spelling across vendors —
    /// Hikvision, Dahua, Amcrest and Axis all name these differently, and even one vendor varies by
    /// firmware. Anything not listed here stays unclassified and simply keeps flowing to the raw
    /// CameraEvents log exactly as before, so an unrecognized topic is never a regression.
    private static readonly (string Marker, DetectionKind Kind)[] DetectionTopicMarkers =
    [
        ("PeopleDetector", DetectionKind.Person),
        ("PersonDetector", DetectionKind.Person),
        ("HumanDetector", DetectionKind.Person),
        ("HumanShapeDetect", DetectionKind.Person),   // Amcrest/Dahua
        ("VehicleDetector", DetectionKind.Vehicle),
        ("CarDetector", DetectionKind.Vehicle),
        ("TrafficDetector", DetectionKind.Vehicle),
        ("FaceDetector", DetectionKind.Face),
        ("FaceRecognition", DetectionKind.Face),
        ("AnimalDetector", DetectionKind.Animal),
        ("PetDetector", DetectionKind.Animal),
        // "Abandoned"/"left" object and its mirror image. Matched before the generic Object* markers
        // below, which would otherwise swallow them — Contains() takes the first hit in this list.
        ("AbandonedObject", DetectionKind.ObjectAppeared),
        ("ObjectAppearance", DetectionKind.ObjectAppeared),
        ("LeftObject", DetectionKind.ObjectAppeared),
        ("MissingObject", DetectionKind.ObjectMissing),
        ("ObjectRemoval", DetectionKind.ObjectMissing),
        ("TakenAway", DetectionKind.ObjectMissing),
        ("ObjectDetector", DetectionKind.Other),
        ("ObjectDetection", DetectionKind.Other)
    ];

    /// <summary>Which object class this topic is about, purely from the topic string — null when
    /// it isn't a recognized object-detection topic at all. Says nothing about whether the object is
    /// currently present; that's IsDetectionActive's job, kept separate so a caller can tell "not
    /// about detection" apart from "detection just ended" (the two would be indistinguishable if
    /// this folded state in, and a falling edge would never close its span).</summary>
    public static DetectionKind? DetectionTopicKind(string? topic)
    {
        if (string.IsNullOrEmpty(topic)) return null;

        foreach (var (marker, kind) in DetectionTopicMarkers)
        {
            if (topic.Contains(marker, StringComparison.OrdinalIgnoreCase)) return kind;
        }
        return null;
    }

    /// <summary>The rising/falling edge for an object-detection notification, read off whichever
    /// boolean state item the payload carries.
    ///
    /// Absent or unparseable state counts as *active*, which is the one place this deliberately
    /// differs from IsMotionActive's "never manufacture motion" stance. Several real firmwares emit
    /// an object-detection notification only when the object appears, carrying data items (ObjectId,
    /// a class name) but no State/IsMotion boolean at all. Treating those as inactive would silently
    /// disable this whole feature on those cameras; treating them as active costs at most a span
    /// left open until the falling edge or the session's own shutdown flush closes it — and unlike
    /// plain motion, an object-detection topic firing at all is already the camera asserting it saw
    /// something.</summary>
    public static bool IsDetectionActive(IReadOnlyDictionary<string, string> simpleItems)
    {
        foreach (var name in new[] { "State", "IsMotion", "Motion" })
        {
            if (simpleItems.TryGetValue(name, out var raw) && bool.TryParse(raw, out var value))
                return value;
        }
        return true;
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
