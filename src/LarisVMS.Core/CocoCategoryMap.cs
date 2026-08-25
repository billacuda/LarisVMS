namespace LarisVMS.Core;

/// <summary>
/// Resolves a raw COCO class name (as reported by YOLO-family object detection, e.g. "car", "dog",
/// "backpack") to one of the small, fixed set of display categories the object detection plan's
/// decision 5 describes — Human, Vehicle, Animal, and Object (the catch-all for everything else
/// COCO's 80-class vocabulary reports). Lives in LarisVMS.Core (not LarisVMS.Vision) so it's usable
/// and testable independent of the inference stack, from either LarisVMS.Vision.Service (which
/// computes it, since it has the raw class string right out of inference) or LarisVMS.Web/
/// Infrastructure (which never needs to re-derive it, but could reference it too without pulling in
/// any GPU/ONNX Runtime dependency).
///
/// A fixed lookup table, not something that grows: COCO's 80 classes are a known, stable
/// vocabulary. An unrecognized name (a future non-COCO model, a typo somewhere upstream) falls
/// through to "Object" rather than throwing or returning null — see DetectedObjectCategory's own
/// doc comment for why an unmapped class landing in the catch-all is the correct, safe default.
/// </summary>
public static class CocoCategoryMap
{
    public const string Human = "Human";
    public const string Vehicle = "Vehicle";
    public const string Animal = "Animal";
    public const string Object = "Object";

    private static readonly HashSet<string> HumanClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "person",
    };

    private static readonly HashSet<string> VehicleClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat",
    };

    private static readonly HashSet<string> AnimalClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "bird", "cat", "dog", "horse", "sheep", "cow", "elephant", "bear", "zebra", "giraffe",
    };

    /// <summary>Resolves a raw class name to Human/Vehicle/Animal/Object. Never throws, never
    /// returns null — an unrecognized name resolves to Object, the same as any of COCO's own
    /// "miscellaneous object" classes (backpack, chair, laptop, ...).</summary>
    public static string Resolve(string cocoClassName)
    {
        ArgumentNullException.ThrowIfNull(cocoClassName);

        if (HumanClasses.Contains(cocoClassName)) return Human;
        if (VehicleClasses.Contains(cocoClassName)) return Vehicle;
        if (AnimalClasses.Contains(cocoClassName)) return Animal;
        return Object;
    }

    /// <summary>Emoji marker for one of these four category names, matching this app's
    /// emoji-as-icons convention (see DetectionDisplay.Emoji, the equivalent for the camera-native
    /// DetectionKind side). Object is the genuine catch-all, same fallback DetectionDisplay itself
    /// uses for its own unclassified "Other" case — never returns anything else for an unrecognized
    /// name, since Resolve above never produces one.</summary>
    public static string Emoji(string categoryName) => categoryName switch
    {
        Human => "🚶",
        Vehicle => "🚗",
        Animal => "🐾",
        _ => "📦"
    };
}
