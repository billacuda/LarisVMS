using System.Xml.Linq;
using LarisVMS.Onvif.Soap;

namespace LarisVMS.Onvif.Clients;

/// <summary>ONVIF ver20 imaging service — picture adjustments (brightness, contrast, saturation,
/// sharpness), day/night (IR cut filter), WDR, backlight compensation, exposure mode, and white
/// balance for one video source.</summary>
public class OnvifImagingClient(OnvifSoapClient soap)
{
    private const string ImagingNs = "http://www.onvif.org/ver20/imaging/wsdl";
    private const string SchemaNs = "http://www.onvif.org/ver10/schema";

    private static readonly string[] SettingsOrder =
    [
        "BacklightCompensation", "Brightness", "ColorSaturation", "Contrast", "Exposure", "Focus",
        "IrCutFilter", "Sharpness", "WideDynamicRange", "WhiteBalance", "Extension",
    ];
    private static readonly string[] ModeLevelOrder = ["Mode", "Level"];
    private static readonly string[] WhiteBalanceOrder = ["Mode", "CrGain", "CbGain", "Extension"];
    private static readonly string[] ExposureOrder =
    [
        "Mode", "Priority", "Window", "MinExposureTime", "MaxExposureTime", "MinGain", "MaxGain",
        "MinIris", "MaxIris", "ExposureTime", "Gain", "Iris",
    ];

    public async Task<OnvifImagingSettings> GetImagingSettingsAsync(
        Uri imagingServiceUri, string videoSourceToken, OnvifCredentials? credentials, CancellationToken ct = default)
    {
        var body = $"""
            <GetImagingSettings xmlns="{ImagingNs}">
              <VideoSourceToken>{Escape(videoSourceToken)}</VideoSourceToken>
            </GetImagingSettings>
            """;
        var response = await soap.PostAsync(imagingServiceUri, $"{ImagingNs}/GetImagingSettings", body, credentials, ct);
        return ParseSettings(response.Child("ImagingSettings") ?? new XElement("ImagingSettings"));
    }

    public async Task<OnvifImagingOptions> GetOptionsAsync(
        Uri imagingServiceUri, string videoSourceToken, OnvifCredentials? credentials, CancellationToken ct = default)
    {
        var body = $"""
            <GetOptions xmlns="{ImagingNs}">
              <VideoSourceToken>{Escape(videoSourceToken)}</VideoSourceToken>
            </GetOptions>
            """;
        var response = await soap.PostAsync(imagingServiceUri, $"{ImagingNs}/GetOptions", body, credentials, ct);
        return ParseOptions(response.Child("ImagingOptions"));
    }

    /// <summary>Sends only the top-level settings the change touches. Each one is sent whole, copied
    /// from the current settings and then patched, so a block with required children (WDR's Mode, for
    /// example) stays valid. Settings that weren't changed are not sent at all, which keeps a device
    /// from rejecting the request over an unrelated value (such as a Focus or Exposure value that
    /// is outside its own range).</summary>
    public async Task SetImagingSettingsAsync(
        Uri imagingServiceUri, string videoSourceToken, OnvifImagingSettings current, OnvifImagingChange change,
        OnvifCredentials? credentials, CancellationToken ct = default)
    {
        var settings = BuildSettingsForSet(current, change);
        var body = $"""
            <SetImagingSettings xmlns="{ImagingNs}">
              <VideoSourceToken>{Escape(videoSourceToken)}</VideoSourceToken>
              {settings}
              <ForcePersistence>true</ForcePersistence>
            </SetImagingSettings>
            """;
        await soap.PostAsync(imagingServiceUri, $"{ImagingNs}/SetImagingSettings", body, credentials, ct);
    }

    internal static OnvifImagingSettings ParseSettings(XElement s) => new(
        Brightness: s.Double("Brightness"),
        ColorSaturation: s.Double("ColorSaturation"),
        Contrast: s.Double("Contrast"),
        Sharpness: s.Double("Sharpness"),
        IrCutFilter: s.Text("IrCutFilter"),
        WideDynamicRangeMode: s.Child("WideDynamicRange").Text("Mode"),
        WideDynamicRangeLevel: s.Child("WideDynamicRange").Double("Level"),
        BacklightCompensationMode: s.Child("BacklightCompensation").Text("Mode"),
        BacklightCompensationLevel: s.Child("BacklightCompensation").Double("Level"),
        ExposureMode: s.Child("Exposure").Text("Mode"),
        WhiteBalanceMode: s.Child("WhiteBalance").Text("Mode"),
        WhiteBalanceCrGain: s.Child("WhiteBalance").Double("CrGain"),
        WhiteBalanceCbGain: s.Child("WhiteBalance").Double("CbGain"))
    { Raw = new XElement(s) };

    internal static OnvifImagingOptions ParseOptions(XElement? o)
    {
        static IReadOnlyList<string> Modes(XElement? e, string name)
            => e.Children(name).Select(m => m.Value.Trim()).Where(m => m.Length > 0).Distinct().ToList();

        return new OnvifImagingOptions(
            Brightness: o.Range("Brightness"),
            ColorSaturation: o.Range("ColorSaturation"),
            Contrast: o.Range("Contrast"),
            Sharpness: o.Range("Sharpness"),
            IrCutFilterModes: Modes(o, "IrCutFilterModes"),
            WideDynamicRangeModes: Modes(o.Child("WideDynamicRange"), "Mode"),
            WideDynamicRangeLevel: o.Child("WideDynamicRange").Range("Level"),
            BacklightCompensationModes: Modes(o.Child("BacklightCompensation"), "Mode"),
            BacklightCompensationLevel: o.Child("BacklightCompensation").Range("Level"),
            ExposureModes: Modes(o.Child("Exposure"), "Mode"),
            WhiteBalanceModes: Modes(o.Child("WhiteBalance"), "Mode"),
            // The options schema calls the gains YrGain/YbGain; the settings schema calls them CrGain/CbGain.
            WhiteBalanceCrGain: o.Child("WhiteBalance").Range("YrGain"),
            WhiteBalanceCbGain: o.Child("WhiteBalance").Range("YbGain"));
    }

    internal static XElement BuildSettingsForSet(OnvifImagingSettings current, OnvifImagingChange change)
    {
        XNamespace tt = current.Raw.Elements().FirstOrDefault()?.Name.Namespace is { } ns && ns != XNamespace.None ? ns : SchemaNs;
        var settings = new XElement(XName.Get("ImagingSettings", ImagingNs));

        // Copies the current element (or starts an empty one) into the outgoing settings at its schema position.
        XElement Take(string name)
        {
            var existing = settings.Child(name);
            if (existing is not null) return existing;
            var copy = current.Raw.Child(name) is { } raw ? new XElement(raw) : new XElement(tt + name);
            var position = Array.IndexOf(SettingsOrder, name);
            var after = settings.Elements().LastOrDefault(e => Array.IndexOf(SettingsOrder, e.Name.LocalName) < position);
            if (after is null) settings.AddFirst(copy); else after.AddAfterSelf(copy);
            return copy;
        }

        void Simple(string name, double? value) { if (value is { } v) Take(name).Value = OnvifXml.Format(v); }

        Simple("Brightness", change.Brightness);
        Simple("ColorSaturation", change.ColorSaturation);
        Simple("Contrast", change.Contrast);
        Simple("Sharpness", change.Sharpness);
        if (change.IrCutFilter is not null) Take("IrCutFilter").Value = change.IrCutFilter;

        if (change.WideDynamicRangeMode is not null || change.WideDynamicRangeLevel is not null)
        {
            var wdr = Take("WideDynamicRange");
            wdr.SetChild("Mode", change.WideDynamicRangeMode ?? current.WideDynamicRangeMode ?? "ON", ModeLevelOrder);
            if (change.WideDynamicRangeLevel is { } l) wdr.SetChild("Level", OnvifXml.Format(l), ModeLevelOrder);
        }
        if (change.BacklightCompensationMode is not null || change.BacklightCompensationLevel is not null)
        {
            var blc = Take("BacklightCompensation");
            blc.SetChild("Mode", change.BacklightCompensationMode ?? current.BacklightCompensationMode ?? "ON", ModeLevelOrder);
            if (change.BacklightCompensationLevel is { } l) blc.SetChild("Level", OnvifXml.Format(l), ModeLevelOrder);
        }
        if (change.ExposureMode is not null) Take("Exposure").SetChild("Mode", change.ExposureMode, ExposureOrder);
        if (change.WhiteBalanceMode is not null || change.WhiteBalanceCrGain is not null || change.WhiteBalanceCbGain is not null)
        {
            var wb = Take("WhiteBalance");
            wb.SetChild("Mode", change.WhiteBalanceMode ?? current.WhiteBalanceMode ?? "AUTO", WhiteBalanceOrder);
            if (change.WhiteBalanceCrGain is { } cr) wb.SetChild("CrGain", OnvifXml.Format(cr), WhiteBalanceOrder);
            if (change.WhiteBalanceCbGain is { } cb) wb.SetChild("CbGain", OnvifXml.Format(cb), WhiteBalanceOrder);
        }
        return settings;
    }

    private static string Escape(string s) => System.Security.SecurityElement.Escape(s);
}
