using System.Text;

namespace LarisVMS.Core.Localization;

/// <summary>
/// Builds the pseudo-locale from the English text: letters swapped for accented look-alikes, padded
/// by about a third, and bracketed — "[Ŝàvé ····]". Text that shows up in plain English while it's
/// selected was never made translatable; a layout that breaks will break for German too.
/// {placeholders} are kept as written.
/// </summary>
public static class PseudoLocale
{
    private const string From_ = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string To___ = "àƀçđéƒĝĥîĵķĺɱñöþǫŕšţûṽŵẋýžÀƁÇĐÉƑĜĤÎĴĶĹṀÑÖÞǪŔŠŢÛṼŴẊÝŽ";

    public static IReadOnlyDictionary<string, string> From(IReadOnlyDictionary<string, string> english) =>
        english.ToDictionary(e => e.Key, e => Convert(e.Value), StringComparer.Ordinal);

    public static string Convert(string text)
    {
        var sb = new StringBuilder(text.Length * 2 + 4);
        sb.Append('[');
        var inPlaceholder = false;
        foreach (var c in text)
        {
            if (c == '{') inPlaceholder = true;
            if (inPlaceholder)
            {
                sb.Append(c);
                if (c == '}') inPlaceholder = false;
                continue;
            }
            var i = From_.IndexOf(c);
            sb.Append(i >= 0 ? To___[i] : c);
        }
        var pad = Math.Max(1, (int)Math.Ceiling(text.Length * 0.3));
        sb.Append(' ').Append('·', pad).Append(']');
        return sb.ToString();
    }
}
