namespace LarisVMS.Core;

/// <summary>Display units for measured speeds (setting <c>Detection.SpeedUnits</c>, global). Speeds
/// are always stored in km/h; this only converts for showing and for entering a threshold.</summary>
public static class SpeedUnits
{
    public const string SettingKey = "Detection.SpeedUnits";
    public const string Kmh = "kmh";
    public const string Mph = "mph";
    public const string Default = Kmh;

    private const double KmPerMile = 1.609344;

    public static string Normalize(string? unit) =>
        string.Equals(unit, Mph, StringComparison.OrdinalIgnoreCase) ? Mph : Kmh;

    public static string Label(string? unit) => Normalize(unit) == Mph ? "mph" : "km/h";

    public static double FromKph(double kph, string? unit) => Normalize(unit) == Mph ? kph / KmPerMile : kph;

    public static double ToKph(double value, string? unit) => Normalize(unit) == Mph ? value * KmPerMile : value;

    /// <summary>"42 km/h" — whole numbers; trap timing isn't more precise than that.</summary>
    public static string Format(double kph, string? unit) =>
        $"{Math.Round(FromKph(kph, unit)):0} {Label(unit)}";
}
