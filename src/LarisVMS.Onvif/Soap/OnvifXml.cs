using System.Globalization;
using System.Xml.Linq;

namespace LarisVMS.Onvif.Soap;

/// <summary>Local-name XML helpers shared by the configuration clients (media, media2, imaging).
/// Every lookup matches by local name, never a fixed namespace — the same reason OnvifSoapClient
/// does: real devices declare schema elements under whichever ONVIF schema version (or vendor
/// prefix) they like.</summary>
internal static class OnvifXml
{
    public static XElement? Child(this XElement? e, string localName)
        => e?.Elements().FirstOrDefault(x => x.Name.LocalName == localName);

    public static IEnumerable<XElement> Children(this XElement? e, string localName)
        => e?.Elements().Where(x => x.Name.LocalName == localName) ?? [];

    public static string? Text(this XElement? e, string localName) => e.Child(localName)?.Value.Trim();

    public static int? Int(this XElement? e, string localName) => ParseInt(e.Text(localName));

    public static double? Double(this XElement? e, string localName) => ParseDouble(e.Text(localName));

    public static int? ParseInt(string? s)
        => s is not null && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v
         : ParseDouble(s) is { } d ? (int)Math.Round(d) : null;

    public static double? ParseDouble(string? s)
        => s is not null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>A tt:IntRange / tt:FloatRange (&lt;Min/&gt;&lt;Max/&gt;) child, or null when absent.</summary>
    public static Clients.OnvifRange? Range(this XElement? e, string localName)
    {
        var r = e.Child(localName);
        var min = r.Double("Min");
        var max = r.Double("Max");
        return min is { } lo && max is { } hi ? new Clients.OnvifRange(lo, hi) : null;
    }

    /// <summary>Whitespace-separated list in an attribute or element text (tt:IntList, tt:FloatList,
    /// tt:StringAttrList) — Media2 options carry several of their lists this way.</summary>
    public static IReadOnlyList<string> SplitList(string? s)
        => string.IsNullOrWhiteSpace(s) ? [] : s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Every whitespace-separated value across all &lt;Items&gt; children (tt:IntItemList) —
    /// devices disagree on one Items element holding a list vs. one Items element per value.</summary>
    public static IReadOnlyList<int> Items(this XElement? e)
        => e.Children("Items").SelectMany(i => SplitList(i.Value)).Select(ParseInt).OfType<int>().ToList();

    public static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    public static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Format(bool value) => value ? "true" : "false";

    /// <summary>Sets a child element's value, or inserts it at its schema position when missing.
    /// <paramref name="schemaOrder"/> is the element's sibling sequence from the ONVIF schema — strict
    /// device parsers reject an xs:sequence out of order, so a new element goes after the last
    /// existing sibling that precedes it rather than simply at the end. New elements take the
    /// namespace of their existing siblings (or the parent's), matching whatever the device used.</summary>
    public static XElement SetChild(this XElement parent, string localName, string? value, IReadOnlyList<string> schemaOrder)
    {
        var existing = parent.Child(localName);
        if (existing is not null)
        {
            if (value is not null) existing.Value = value;
            return existing;
        }

        var ns = parent.Elements().FirstOrDefault()?.Name.Namespace ?? parent.Name.Namespace;
        var created = value is null ? new XElement(ns + localName) : new XElement(ns + localName, value);

        var position = IndexOf(schemaOrder, localName);
        var before = parent.Elements().LastOrDefault(x => IndexOf(schemaOrder, x.Name.LocalName) is var i && i >= 0 && i < position);
        if (before is not null) before.AddAfterSelf(created);
        else if (position >= 0 && parent.Elements().FirstOrDefault(x => IndexOf(schemaOrder, x.Name.LocalName) > position) is { } after)
            after.AddBeforeSelf(created);
        else parent.Add(created);
        return created;
    }

    private static int IndexOf(IReadOnlyList<string> order, string localName)
    {
        for (var i = 0; i < order.Count; i++) if (order[i] == localName) return i;
        return -1;
    }
}
