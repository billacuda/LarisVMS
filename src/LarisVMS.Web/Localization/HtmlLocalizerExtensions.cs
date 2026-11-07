using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using LarisVMS.Core.Localization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Html;
using Microsoft.Extensions.Localization;

namespace LarisVMS.Web.Localization;

/// <summary>
/// Text that keeps a little inline markup, so a sentence with a code sample, a bold word, or a link
/// in the middle stays one translatable sentence: "Split it into {count} cameras. &lt;a href=\"/Help/cameras\"&gt;Learn more&lt;/a&gt;".
/// Only these tags survive, with no attributes except a relative href on &lt;a&gt;: code, strong, em,
/// b, i, kbd, br, and a. Everything else in the text, and every argument, is HTML-encoded, so a
/// language file can't inject script or styling.
/// </summary>
public static partial class HtmlLocalizerExtensions
{
    [GeneratedRegex(@"</?(?:code|strong|em|b|i|kbd)>|<br\s*/?>|<a href=""(?:/(?!/)|#)[^""<>]*"">|</a>|\{([A-Za-z_][A-Za-z0-9_]*|\d+)\}", RegexOptions.IgnoreCase)]
    private static partial Regex SafeTokenRegex();

    /// <summary>For Razor Pages (.cshtml).</summary>
    public static HtmlString Html(this IStringLocalizer localizer, string key, object? args = null) =>
        new(Render(localizer[key].Value, args));

    /// <summary>For Razor components (.razor).</summary>
    public static MarkupString Markup(this IStringLocalizer localizer, string key, object? args = null) =>
        new(Render(localizer[key].Value, args));

    /// <summary>Plural text with markup, for Razor Pages.</summary>
    public static HtmlString PluralHtml(this IStringLocalizer localizer, string baseKey, long count, object? args = null) =>
        new(RenderPlural(baseKey, count, args));

    /// <summary>Plural text with markup, for Razor components.</summary>
    public static MarkupString PluralMarkup(this IStringLocalizer localizer, string baseKey, long count, object? args = null) =>
        new(RenderPlural(baseKey, count, args));

    private static string RenderPlural(string baseKey, long count, object? args)
    {
        var culture = CultureInfo.CurrentUICulture;
        var template = Loc.Catalog.FindPlural(culture.Name, baseKey, count) ?? baseKey;
        var named = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (args is not null) foreach (var (k, v) in LocaleFormatter.ToNamed([args])) named[k] = v;
        named.TryAdd("count", count);
        return Render(template, named);
    }

    /// <summary>The template with safe tags kept, placeholders filled (encoded), and everything else encoded.</summary>
    public static string Render(string template, object? args)
    {
        var culture = CultureInfo.CurrentUICulture;
        var named = args is null ? new Dictionary<string, object?>() : LocaleFormatter.ToNamed([args]);
        var sb = new StringBuilder(template.Length + 32);
        var pos = 0;
        foreach (Match m in SafeTokenRegex().Matches(template))
        {
            sb.Append(WebUtility.HtmlEncode(template[pos..m.Index]));
            if (m.Groups[1].Success)
            {
                // Markup the page built itself (a <time> element, a link) goes in as is; anything else is text.
                if (named.TryGetValue(m.Groups[1].Value, out var value))
                    sb.Append(value switch
                    {
                        MarkupString markup => markup.Value,
                        IHtmlContent html => ToHtml(html),
                        IFormattable f => WebUtility.HtmlEncode(f.ToString(null, culture)),
                        _ => WebUtility.HtmlEncode(value?.ToString() ?? "")
                    });
                else
                    sb.Append(WebUtility.HtmlEncode(m.Value));
            }
            else
            {
                sb.Append(m.Value);
            }
            pos = m.Index + m.Length;
        }
        sb.Append(WebUtility.HtmlEncode(template[pos..]));
        return sb.ToString();
    }

    private static string ToHtml(IHtmlContent content)
    {
        using var writer = new StringWriter();
        content.WriteTo(writer, System.Text.Encodings.Web.HtmlEncoder.Default);
        return writer.ToString();
    }
}
