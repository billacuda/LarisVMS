namespace LarisVMS.Core;

/// <summary>
/// A vendor-specific camera integration — the extensibility point for everything ONVIF can't
/// express. Providers are compiled in and registered in <see cref="CameraIntegrations.All"/> rather
/// than loaded from external assemblies at runtime: recorder nodes ship as a single self-contained
/// executable that auto-updates by file swap (see build-node.ps1 / NodeUpdater), so a drop-in
/// plugin folder would need a second distribution and version-matching channel, and would mean
/// loading arbitrary code onto recorder machines. Adding a vendor is one descriptor here plus one
/// session in LarisVMS.Node, both listed in exactly one place.
///
/// This exists because ONVIF is the floor, not the ceiling. Confirmed against this deployment's own
/// fleet: six Amcrest AI cameras with Smart Motion Detection switched on emit *no* object-detection
/// topic over ONVIF at all — 21,747 recorded events containing only CellMotion/MotionAlarm/Tamper/
/// Monitoring topics — and their ONVIF metadata track carries a MotionInCells grid rather than
/// object geometry. The camera really is classifying people and vehicles; it just publishes that
/// through the vendor's own API instead.
///
/// Deliberately split: this half is pure matching with no I/O, so the Web tier can decide during
/// probing whether a camera needs an integration, while the session that actually talks to the
/// device lives in LarisVMS.Node (which is where camera network access belongs). The two halves are
/// joined by <see cref="Key"/>.
/// </summary>
public interface ICameraIntegrationProvider
{
    /// <summary>Stable identifier persisted on the camera row and sent to nodes in their config —
    /// renaming one orphans every camera already using it, so treat it as permanent.</summary>
    string Key { get; }

    string DisplayName { get; }

    /// <summary>One line an admin can read to understand what turning this on actually gets them.</summary>
    string Summary { get; }

    /// <summary>This provider's own version, independent of the application's — a provider changes
    /// when its vendor's API or event-code table does, which has nothing to do with the release
    /// cadence of everything around it. Starts at 1.0.0 and is bumped whenever the provider's own
    /// behavior changes, so `Admin → Plugins` can answer "which version of the Dahua integration is
    /// this deployment actually running" without reading the changelog. Shown as-is, so keep it
    /// parseable as a normal three-part version.</summary>
    string Version { get; }

    /// <summary>Whether this provider handles a camera reporting this make/model over ONVIF
    /// (Camera.Manufacturer/Model, populated by GetDeviceInformation on every probe).</summary>
    bool Supports(string? manufacturer, string? model);
}

/// <summary>The registry. One list, checked in order — first match wins, so a more specific
/// provider must be registered ahead of a broader one.</summary>
public static class CameraIntegrations
{
    public static IReadOnlyList<ICameraIntegrationProvider> All { get; } =
    [
        new DahuaCgiIntegrationProvider()
    ];

    /// <summary>The provider for a camera's reported make/model, or null when plain ONVIF is all it
    /// needs. Called during probing, so a camera picks up (or loses) an integration automatically as
    /// its reported identity changes — no manual selection to keep in sync.</summary>
    public static ICameraIntegrationProvider? Detect(string? manufacturer, string? model)
        => All.FirstOrDefault(p => p.Supports(manufacturer, model));

    /// <summary>Null for an unknown key rather than throwing: a camera row can outlive the provider
    /// that set it (a downgrade, or a provider removed in a later version), and that must degrade to
    /// "no integration" instead of breaking config generation for the whole node.</summary>
    public static ICameraIntegrationProvider? ByKey(string? key)
        => string.IsNullOrWhiteSpace(key) ? null
            : All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Dahua's CGI event API, which Amcrest and several other rebadges also speak — the first
/// provider, and the reason this extensibility point exists at all.</summary>
public sealed class DahuaCgiIntegrationProvider : ICameraIntegrationProvider
{
    public const string ProviderKey = "dahua-cgi";

    public string Key => ProviderKey;
    public string DisplayName => "Dahua / Amcrest smart events";

    /// <summary>1.0.0 — the provider as first shipped, plus the object-class expansion that mapped
    /// LeftDetection/TakenAwayDetection to Object appeared/missing and added animal and pet codes.
    /// Bump this whenever the code table or the session's behavior changes.</summary>
    public string Version => "1.0.0";

    public string Summary =>
        "Reads person and vehicle detections from the camera's own Smart Motion Detection over " +
        "Dahua's CGI event API. These cameras classify objects onboard but don't publish that over " +
        "ONVIF, so without this only plain motion is available.";

    /// <summary>Matched on manufacturer rather than model: the CGI endpoint is present across the
    /// whole line, and a model without object analytics simply never sends an object event — which
    /// costs one idle long-poll rather than a wrong answer. (This fleet's AI-capable models happen to
    /// carry an "-AI" suffix, e.g. IP5M-B1276EW-AI, but keying on that would be guessing at a naming
    /// convention the vendor never promised.)</summary>
    private static readonly string[] Manufacturers = ["dahua", "amcrest", "lorex", "empiretech"];

    public bool Supports(string? manufacturer, string? model)
    {
        var haystack = $"{manufacturer} {model}";
        return Manufacturers.Any(m => haystack.Contains(m, StringComparison.OrdinalIgnoreCase));
    }
}
