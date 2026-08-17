using System.Text.RegularExpressions;

namespace LarisVMS.Core;

/// <summary>Resolved, already-validated branding for the shared layout. Every field here is safe to
/// emit into the page as-is; validation happens once on the way out of storage (see Sanitize) rather
/// than at each render site, because these values are interpolated into a &lt;style&gt; block and a
/// stored value that skipped validation would be a CSS-injection vector reachable by anyone with
/// Settings.Edit.</summary>
public record BrandingOptions(
    string AppName,
    string? PrimaryColor,
    string? AccentColor,
    string? LogoDataUri,
    string? FontKey)
{
    public const string DefaultAppName = "LarisVMS";

    public static BrandingOptions Default { get; } = new(DefaultAppName, null, null, null, null);

    /// <summary>The CSS font stack for the selected key, or null to leave the page on Bootstrap's own
    /// default — never the raw stored value, so nothing user-supplied reaches the stylesheet.</summary>
    public string? FontStack => Branding.FontStackFor(FontKey);
}

/// <summary>Validation for admin-editable branding. Deliberately allowlist-based throughout: a color
/// must match a hex literal, a font is chosen by key from a fixed table (the CSS stack is this
/// codebase's own string, never the user's), and a logo must be a base64 image data URI within a
/// size cap. Anything failing validation degrades to "unset" rather than throwing — bad branding
/// should never be able to take the whole site down, since the layout renders on every page.</summary>
public static class Branding
{
    /// <summary>#rgb or #rrggbb. Deliberately not accepting arbitrary CSS color syntax
    /// (rgb()/hsl()/named/var()): the value is interpolated into a style block, and a strict literal
    /// is the cheapest way to guarantee nothing else can ride along with it.</summary>
    private static readonly Regex HexColor = new(@"^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$", RegexOptions.Compiled);

    /// <summary>Roughly 512KB of base64 (~384KB of image). Large enough for any sane logo, small
    /// enough that it can't bloat every page render — the data URI is inlined into the layout, so it
    /// is paid on every request by every user.</summary>
    public const int MaxLogoDataUriLength = 512 * 1024;

    /// <summary>Key → CSS stack. The key is what's stored and what the admin form posts; the stack is
    /// only ever this table's own value, so a stored key that isn't in the table simply resolves to
    /// null (Bootstrap's default) instead of reaching the page.</summary>
    public static IReadOnlyDictionary<string, string> FontStacks { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["system"] = "system-ui, -apple-system, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif",
        ["inter"] = "Inter, 'Segoe UI', system-ui, sans-serif",
        ["helvetica"] = "'Helvetica Neue', Helvetica, Arial, sans-serif",
        ["verdana"] = "Verdana, Geneva, sans-serif",
        ["georgia"] = "Georgia, 'Times New Roman', serif",
        ["mono"] = "Consolas, 'Courier New', monospace"
    };

    /// <summary>Human labels for the admin page's font picker, in display order.</summary>
    public static IReadOnlyList<(string Key, string Label)> FontChoices { get; } =
    [
        ("system", "System default"),
        ("inter", "Inter / Segoe UI"),
        ("helvetica", "Helvetica / Arial"),
        ("verdana", "Verdana"),
        ("georgia", "Georgia (serif)"),
        ("mono", "Consolas (monospace)")
    ];

    public static string? FontStackFor(string? fontKey)
        => fontKey is not null && FontStacks.TryGetValue(fontKey, out var stack) ? stack : null;

    public static string? NormalizeColor(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) || !HexColor.IsMatch(trimmed) ? null : trimmed;
    }

    public static string? NormalizeFontKey(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) || !FontStacks.ContainsKey(trimmed) ? null : trimmed.ToLowerInvariant();
    }

    /// <summary>Accepts only a base64 image data URI, and only of a format a browser will actually
    /// render inline. Rejects svg deliberately: an SVG can carry script, and this string is emitted
    /// straight into an img src on every page.</summary>
    public static string? NormalizeLogoDataUri(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed.Length > MaxLogoDataUriLength) return null;

        var match = LogoDataUri.Match(trimmed);
        if (!match.Success) return null;

        // Confirm the payload actually decodes rather than trusting the character class alone — a
        // string that merely looks base64-ish but is malformed would render as a broken image on
        // every page, which is worse than rejecting it at save time.
        try
        {
            _ = Convert.FromBase64String(match.Groups[2].Value);
            return trimmed;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static readonly Regex LogoDataUri =
        new(@"^data:image/(png|jpeg|jpg|gif|webp);base64,([A-Za-z0-9+/]+={0,2})$", RegexOptions.Compiled);

    public static string NormalizeAppName(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? BrandingOptions.DefaultAppName : trimmed;
    }
}
