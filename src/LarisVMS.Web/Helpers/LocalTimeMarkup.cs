using System.Globalization;

namespace LarisVMS.Web.Helpers;

/// <summary>
/// Server-rendered times in the viewer's local time. A page renders
/// <c>&lt;time datetime="@x.ToUtcIso()" data-local="g"&gt;@x.ToLocalTime().ToString("g")&lt;/time&gt;</c>
/// and local-time.js rewrites the text in the browser's own time zone. <c>ToLocalTime()</c> on its own
/// uses the *server's* zone, which is wrong for anyone viewing from elsewhere; it stays only as the
/// text shown if scripts don't run. data-local is "g" (date and time), "G" (with seconds), or "d"
/// (date only), matching the .NET format the fallback uses.
/// </summary>
public static class LocalTimeMarkup
{
    /// <summary>ISO 8601 UTC with a "Z". Values read from the database come back as Unspecified but
    /// are stored as UTC, so Unspecified is treated as UTC.</summary>
    public static string ToUtcIso(this DateTime value)
        => (value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc))
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>The whole &lt;time&gt; element, for a time in the middle of a translated sentence:
    /// <c>@L.Markup("key", new { time = x.ToTimeTag() })</c>.</summary>
    public static Microsoft.AspNetCore.Components.MarkupString ToTimeTag(this DateTime value, string style = "g")
        => new($"<time datetime=\"{value.ToUtcIso()}\" data-local=\"{System.Net.WebUtility.HtmlEncode(style)}\">" +
               $"{System.Net.WebUtility.HtmlEncode(value.ToLocalTime().ToString(style, CultureInfo.CurrentCulture))}</time>");
}
