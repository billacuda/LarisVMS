using System.Globalization;
using LarisVMS.Core;

namespace LarisVMS.Web.Helpers;

/// <summary>
/// Builds the CSS frame for a Snapshots-page card from its badge color(s). A card carrying a single
/// distinct color keeps the plain 1px <c>border-color</c> it has always had; a card that grouped two
/// or more differently-colored AI-detection badges (a person and a dog in one frame) gets a 3px
/// gradient ring instead, anchored so the lowest-hue color sits at the upper-left corner and the
/// others follow clockwise.
///
/// Every value here is interpolated into a style attribute, so colors are validated through
/// <see cref="Branding.NormalizeColor"/> first — anything that is not a strict hex literal is dropped
/// rather than passed through.
/// </summary>
public static class SnapshotBorderStyle
{
    /// <summary>
    /// The distinct, validated badge colors in the order they should wrap the card: normalized to
    /// lowercase <c>#rrggbb</c>, de-duplicated by first appearance, then sorted ascending by HSL hue
    /// so the lowest-hue color anchors the upper-left corner and the rest run clockwise from there.
    /// The hex string is a stable tie-break, so equal-hue inputs order deterministically.
    ///
    /// Known simplification: hue is a plain circle, so a near-360° crimson sorts last rather than
    /// next to a 0° red. Acceptable — the curated palette has one red, not two.
    /// </summary>
    public static IReadOnlyList<string> OrderColors(IEnumerable<string?> badgeHexes)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var distinct = new List<string>();
        foreach (var raw in badgeHexes)
        {
            var normalized = Expand(Branding.NormalizeColor(raw));
            if (normalized is not null && seen.Add(normalized))
                distinct.Add(normalized);
        }

        return distinct
            .OrderBy(Hue)
            .ThenBy(hex => hex, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The CSS <c>background</c> value for the gradient ring, or <c>null</c> when there are fewer than
    /// two distinct colors and the caller should keep the solid border. Angles are re-based with
    /// <c>from 315deg</c> so gradient-0 points at the upper-left screen corner; the corner directions
    /// from the card center are UL 315°, UR 45°, LR 135°, LL 225°.
    /// </summary>
    public static string? GradientCss(IReadOnlyList<string> ordered)
    {
        if (ordered.Count < 2)
            return null;

        if (ordered.Count == 2)
            return $"linear-gradient(135deg, {ordered[0]} 0%, {ordered[1]} 100%)";

        if (ordered.Count == 3)
            return $"conic-gradient(from 315deg at 50% 50%, {ordered[0]} 0deg, {ordered[1]} 90deg, "
                 + $"{ordered[2]} 225deg, {ordered[0]} 360deg)";

        if (ordered.Count == 4)
            return $"conic-gradient(from 315deg at 50% 50%, {ordered[0]} 0deg, {ordered[1]} 90deg, "
                 + $"{ordered[2]} 180deg, {ordered[3]} 270deg, {ordered[0]} 360deg)";

        // 5+ badges on one grouped card is vanishingly rare — spread the stops evenly and wrap.
        var stops = new List<string>(ordered.Count + 1);
        for (var i = 0; i < ordered.Count; i++)
        {
            var deg = (int)Math.Round(i * 360.0 / ordered.Count, MidpointRounding.AwayFromZero);
            stops.Add($"{ordered[i]} {deg}deg");
        }
        stops.Add($"{ordered[0]} 360deg");
        return $"conic-gradient(from 315deg at 50% 50%, {string.Join(", ", stops)})";
    }

    /// <summary>Expands a validated <c>#rgb</c> to <c>#rrggbb</c> and lowercases; passes a 6-digit
    /// value straight through lowercased. Input is assumed already validated by
    /// <see cref="Branding.NormalizeColor"/> — a null stays null.</summary>
    private static string? Expand(string? normalized)
    {
        if (normalized is null)
            return null;

        var hex = normalized[1..];
        if (hex.Length == 3)
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        return "#" + hex.ToLowerInvariant();
    }

    /// <summary>Hue in <c>[0, 360)</c> for a <c>#rrggbb</c> string, standard HSL formula. A gray
    /// (chroma 0) has no meaningful hue and returns 0.</summary>
    private static double Hue(string hex)
    {
        var r = int.Parse(hex.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
        var g = int.Parse(hex.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
        var b = int.Parse(hex.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var chroma = max - min;
        if (chroma == 0)
            return 0;

        double hue;
        if (r >= g && r >= b)
            hue = ((g - b) / chroma) % 6;
        else if (g >= b)
            hue = (b - r) / chroma + 2;
        else
            hue = (r - g) / chroma + 4;

        hue *= 60;
        return hue < 0 ? hue + 360 : hue;
    }
}
