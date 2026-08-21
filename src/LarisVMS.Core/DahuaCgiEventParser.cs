using LarisVMS.Core.Enums;

namespace LarisVMS.Core;

/// <summary>One decoded event from Dahua's CGI event stream.</summary>
/// <param name="Code">The vendor's own event code, e.g. "SmartMotionHuman".</param>
/// <param name="Action">"Start", "Stop" or "Pulse".</param>
/// <param name="Index">Channel index the event belongs to (0 on a single-sensor camera).</param>
/// <param name="ObjectType">What the camera says it saw, read out of the event's own <c>data</c>
/// payload (e.g. "Human", "Vehicle", "HumanFace") — null for an event whose payload carries none,
/// which is every event on firmware that doesn't publish object detail. This is what lets a generic
/// IVS rule (one <c>CrossRegionDetection</c> code covering both people and cars) still classify each
/// event correctly, rather than collapsing to a single "an object was seen".</param>
public readonly record struct DahuaCgiEvent(string Code, string Action, int Index, string? ObjectType = null)
{
    /// <summary>A momentary event with no matching Stop — the span is opened and closed together.</summary>
    public bool IsPulse => string.Equals(Action, "Pulse", StringComparison.OrdinalIgnoreCase);

    public bool IsStart => IsPulse || string.Equals(Action, "Start", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Parses Dahua/Amcrest's CGI event stream (<c>/cgi-bin/eventManager.cgi?action=attach</c>), which
/// is a never-ending <c>multipart/x-mixed-replace</c> body of parts shaped like:
///
/// <code>
/// --myboundary
/// Content-Type: text/plain
/// Content-Length: 40
///
/// Code=SmartMotionHuman;action=Start;index=0
/// </code>
///
/// Deliberately pure and line-oriented rather than a streaming HTTP reader: the transport is trivial
/// (read lines until cancelled) but the payload grammar and code mapping are where the real risk is,
/// and this way both are testable against captured samples with no camera present. That matters
/// more than usual here — this integration exists precisely because the hardware's behavior can't be
/// assumed from documentation, as the ONVIF spike proved.
/// </summary>
public static class DahuaCgiEventParser
{
    /// <summary>Vendor code → what was actually seen. Dahua's own naming is inconsistent across
    /// firmware generations (SmartMotionHuman on newer AI models, HumanDetect/FaceDetection on
    /// others, and the IVS rules CrossLineDetection/CrossRegionDetection which fire for *any* object
    /// crossing a configured line or area). Anything unlisted parses fine and simply classifies as
    /// null — the raw code is still reported, so an unrecognized event is visible rather than
    /// silently dropped, exactly like an unrecognized ONVIF topic.</summary>
    private static readonly (string Code, DetectionKind Kind)[] CodeMap =
    [
        ("SmartMotionHuman", DetectionKind.Person),
        ("HumanDetect", DetectionKind.Person),
        ("HumanTrait", DetectionKind.Person),
        ("SmartMotionVehicle", DetectionKind.Vehicle),
        ("VehicleDetect", DetectionKind.Vehicle),
        ("TrafficJunction", DetectionKind.Vehicle),
        ("FaceDetection", DetectionKind.Face),
        ("FaceRecognition", DetectionKind.Face),
        ("AnimalDetection", DetectionKind.Animal),
        ("SmartMotionAnimal", DetectionKind.Animal),
        ("PetDetection", DetectionKind.Animal),
        // Dahua's own names for the abandoned-object pair: something was left behind, or something
        // that was there is gone. Previously both landed in Other, which lost the distinction.
        ("LeftDetection", DetectionKind.ObjectAppeared),
        ("AbandonedObjectDetection", DetectionKind.ObjectAppeared),
        ("TakenAwayDetection", DetectionKind.ObjectMissing),
        ("MissingObjectDetection", DetectionKind.ObjectMissing),
        // IVS rules: an object crossed a tripwire or entered an area. Real object detection, but the
        // code itself doesn't say which class, so it maps to Other rather than guessing.
        ("CrossLineDetection", DetectionKind.Other),
        ("CrossRegionDetection", DetectionKind.Other)
    ];

    /// <summary>Subscribed but never classified — these exist purely so the connection carries some
    /// traffic. The session treats a long silence as a dead socket and reconnects, which was firing
    /// on every camera every 3 minutes: the narrow subscription below excludes the codes these
    /// cameras actually emit most of the time, so a perfectly healthy connection looked dead. Each
    /// reconnect is a window where a detection is lost outright (attach only delivers from the moment
    /// it subscribes), so the churn wasn't merely noisy.
    ///
    /// VideoMotionInfo is the right choice for this: it's a bare <c>action=State</c> ping carrying
    /// nothing but a timestamp, it fires whenever there's any activity at all, and it maps to no
    /// DetectionKind — so it can never be mistaken for a detection. Plain VideoMotion is deliberately
    /// still excluded (see below); this is its info-only sibling, not the motion event itself.</summary>
    private static readonly string[] KeepAliveCodes = ["VideoMotionInfo"];

    /// <summary>The codes worth subscribing to. Passed to the camera as the <c>codes=[...]</c> query
    /// parameter — narrower than <c>[All]</c> on purpose, since All also streams every heartbeat,
    /// storage and config-change event the camera produces, which is a lot of traffic for nothing.
    /// VideoMotion is deliberately absent: plain motion already arrives over ONVIF, and taking it
    /// from both sources would double-report the same event.</summary>
    public static string SubscribeCodes =>
        string.Join(',', CodeMap.Select(c => c.Code).Distinct().Concat(KeepAliveCodes));

    /// <summary>Null when the line isn't an event at all (a boundary marker, a header, or the blank
    /// line between them) — the caller feeds every line in and lets this decide.</summary>
    public static DahuaCgiEvent? ParseLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        var trimmed = line.Trim();
        if (!trimmed.StartsWith("Code=", StringComparison.OrdinalIgnoreCase)) return null;

        string? code = null, action = null;
        var index = 0;

        foreach (var field in trimmed.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = field.IndexOf('=');
            if (separator <= 0) continue;

            var name = field[..separator].Trim();
            var value = field[(separator + 1)..].Trim();

            if (name.Equals("Code", StringComparison.OrdinalIgnoreCase)) code = value;
            else if (name.Equals("action", StringComparison.OrdinalIgnoreCase)) action = value;
            else if (name.Equals("index", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var i)) index = i;
            // "data={...}" is deliberately ignored: it's a JSON blob whose shape varies by code and
            // firmware, and nothing here needs it. It can also contain ';', which is why this splits
            // fields but never tries to reassemble that value.
        }

        // Both are required — a line naming a code with no action says nothing about an edge.
        return code is null || action is null ? null : new DahuaCgiEvent(code, action, index);
    }

    /// <summary>What this event means, or null for a code this app doesn't model. Matched exactly
    /// (not by substring) because Dahua's codes are a closed vocabulary, unlike ONVIF's
    /// vendor-extended topic strings.</summary>
    public static DetectionKind? Classify(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        foreach (var (candidate, kind) in CodeMap)
        {
            if (string.Equals(candidate, code, StringComparison.OrdinalIgnoreCase)) return kind;
        }
        return null;
    }

    /// <summary>The object class named in an event's own payload, which is strictly better
    /// information than its code when both are available: an IVS rule fires one code
    /// (CrossRegionDetection) for every class it's configured to match, so code alone can only ever
    /// say "an object", while the payload says which. Confirmed against this fleet's IP8M cameras,
    /// whose intrusion rule is configured for Human *and* Vehicle and reports the actual one per
    /// event.</summary>
    public static DetectionKind? ClassifyObjectType(string? objectType) =>
        objectType?.Trim().ToLowerInvariant() switch
        {
            "human" or "person" or "pedestrian" => DetectionKind.Person,
            "vehicle" or "car" or "motor" or "nonmotor" => DetectionKind.Vehicle,
            // Dahua's face events report HumanFace; kept mapping to Face rather than Person so a
            // face event stays distinguishable from a whole-body detection, matching FaceDetection's
            // own entry in CodeMap above.
            "humanface" or "face" => DetectionKind.Face,
            "animal" or "pet" or "dog" or "cat" => DetectionKind.Animal,
            _ => null
        };

    // "ObjectType" : "Human" — tolerant of the whitespace Dahua's pretty-printed JSON puts around
    // the colon. Deliberately a regex over the raw text rather than a JSON parse: the payload's shape
    // varies by code and firmware (some carry Object, some Objects[], some both, and the outer
    // envelope differs), and every one of those spellings puts the class in a field of this exact
    // name — so matching the field directly is both simpler and more robust than modelling any
    // particular envelope. The first occurrence wins; where a payload repeats it across Object and
    // Objects[0] (as this fleet's do) they agree.
    private static readonly System.Text.RegularExpressions.Regex ObjectTypePattern =
        new("\"ObjectType\"\\s*:\\s*\"([^\"]+)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static string? ExtractObjectType(string? payload)
    {
        if (string.IsNullOrEmpty(payload)) return null;
        var match = ObjectTypePattern.Match(payload);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Parses a whole event — its <c>Code=…</c> header line plus however many lines of
    /// <c>data={…}</c> JSON follow it. ParseLine below handles only the header, which is all this
    /// needed before object detail mattered; the session accumulates the full text and calls this so
    /// the payload's own class can be read. Null for anything that isn't an event.</summary>
    public static DahuaCgiEvent? ParseEvent(string? fullText)
    {
        if (string.IsNullOrWhiteSpace(fullText)) return null;

        // The header is the first line; everything after it is payload. Split on the first newline
        // rather than trimming, so a single-line event (no data blob) still works unchanged.
        var newline = fullText.IndexOf('\n');
        var header = newline < 0 ? fullText : fullText[..newline];

        if (ParseLine(header) is not { } evt) return null;
        return evt with { ObjectType = ExtractObjectType(fullText) };
    }
}
