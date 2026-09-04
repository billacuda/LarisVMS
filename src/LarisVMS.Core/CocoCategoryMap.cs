using LarisVMS.Core.Enums;

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
/// Originally "a fixed lookup table, not something that grows: COCO's 80 classes are a known,
/// stable vocabulary" — no longer strictly true since the D-FINE integration added an Objects365
/// vocabulary option (DFineWeights.Obj365, 365 classes) alongside COCO's 80, but the name and
/// 4-bucket shape stayed: both vocabularies still resolve into the same Human/Vehicle/Animal/Object
/// buckets, just from a larger combined word list. An unrecognized name (a genuinely new model, a
/// typo somewhere upstream) falls through to "Object" rather than throwing or returning null — see
/// DetectedObjectCategory's own doc comment for why an unmapped class landing in the catch-all is
/// the correct, safe default.
/// </summary>
public static class CocoCategoryMap
{
    public const string Human = "Human";
    public const string Vehicle = "Vehicle";
    public const string Animal = "Animal";
    public const string Object = "Object";

    // "person" (COCO/obj2coco) and "Person" (Objects365) are the same word under the
    // case-insensitive comparer below — only one entry is needed.
    private static readonly HashSet<string> HumanClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "person",
    };

    // COCO/obj2coco and Objects365 combined — the same flat lookup regardless of which model
    // produced the raw string, since Resolve doesn't know (or need to know) which vocabulary it's
    // seeing. "motorbike"/"aeroplane" are D-FINE's own COCO legacy PASCAL-VOC-era spellings (see
    // DFineLabels' own doc comment) — without them, a motorbike or aeroplane detection silently
    // fell through to the "Object" catch-all instead of "Vehicle".
    private static readonly HashSet<string> VehicleClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        // COCO / obj2coco
        "bicycle", "car", "motorcycle", "motorbike", "airplane", "aeroplane", "bus", "train", "truck", "boat",
        // Objects365
        "SUV", "Van", "Heavy Truck", "Pickup Truck", "Motorcycle", "Tricycle", "Scooter",
        "Helicopter", "Hot-air balloon", "Sailboat", "Ship", "Ambulance", "Fire Truck",
        "Sports Car", "Formula 1", "Rickshaw", "Carriage", "Machinery Vehicle", "Trolley",
        "Hoverboard",
    };

    private static readonly HashSet<string> AnimalClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        // COCO / obj2coco
        "bird", "cat", "dog", "horse", "sheep", "cow", "elephant", "bear", "zebra", "giraffe",
        // Objects365
        "Lion", "Monkey", "Rabbit", "Pig", "Donkey", "Camel", "Yak", "Antelope", "Deer",
        "Wild Bird", "Duck", "Goose", "Chicken", "Parrot", "Pigeon", "Penguin", "Seal",
        "Dolphin", "Other Fish", "Goldfish", "Jellyfish", "Crab", "Lobster", "Shrimp",
        "Scallop", "Oyster", "Swan",
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

    /// <summary>
    /// The camera-native <see cref="DetectionKind"/> this AI category is the same concept as, or null
    /// for a category name that has no equivalent.
    ///
    /// These are two independent pipelines that name the same things: a camera's own onboard
    /// classifier reports a DetectionKind, and the AI detector resolves a raw class string into one of
    /// the four buckets above. "A person was here" is the same event to a viewer either way, so the
    /// admin's Events colour for Human has to govern both — otherwise the same object is one colour on
    /// a camera-classified span and another on an AI-detected one, which is exactly what a per-category
    /// stored colour (auto-assigned by DetectedObjectColorAssigner, never shown in the Events editor)
    /// produced. <see cref="EventPalette.ColorForAiCategory"/> is what consumes this.
    ///
    /// Object maps to DetectionKind.Other deliberately — Other's own display label *is* "Object", and
    /// both are the same "something we don't model specifically" catch-all.
    /// </summary>
    public static DetectionKind? ToDetectionKind(string categoryName) => categoryName switch
    {
        Human => DetectionKind.Human,
        Vehicle => DetectionKind.Vehicle,
        Animal => DetectionKind.Animal,
        Object => DetectionKind.Other,
        _ => null
    };
}
