using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;

namespace LarisVMS.Core.Localization;

/// <summary>
/// UI text from C# that has no localizer injected: page models, minimal-API error replies,
/// Infrastructure services' exception messages and alert/email text. In the current UI language
/// (<see cref="CultureInfo.CurrentUICulture"/>), which a web request gets from the language
/// middleware and background work sets with <see cref="InLanguage"/>.
/// The web app hands its catalog over at startup (<see cref="Use"/>); until then (tests, tools)
/// the built-in English is used.
/// </summary>
public static class Loc
{
    private static LocaleCatalog? _catalog;
    private static readonly Lazy<LocaleCatalog> EnglishOnly = new(() =>
        new LocaleCatalog(null, null, LocaleCatalog.LoadEmbeddedEnglish(), NullLogger.Instance, watch: false));

    public static void Use(LocaleCatalog catalog) => _catalog = catalog;

    public static LocaleCatalog Catalog => _catalog ?? EnglishOnly.Value;

    /// <summary>The server's default language (Settings → Language), kept current by the web app.
    /// Used for text nobody in particular reads in their own language: team alert channels, and
    /// people who never picked a language.</summary>
    public static string? DefaultLanguage { get; set; }

    /// <summary>The text for <paramref name="key"/> with its {placeholders} filled from
    /// <paramref name="args"/> (an object whose properties name them). The key itself if it doesn't exist.</summary>
    public static string T(string key, object? args = null)
    {
        var culture = CultureInfo.CurrentUICulture;
        var template = Catalog.Find(culture.Name, key) ?? key;
        return args is null ? template : LocaleFormatter.Format(template, culture, args);
    }

    /// <summary>Like <see cref="T"/>, but <paramref name="fallback"/> when no language (English
    /// included) has the key — for names that can be anything, such as a custom model's class names.</summary>
    public static string TOr(string key, string fallback, object? args = null)
    {
        var culture = CultureInfo.CurrentUICulture;
        var template = Catalog.Find(culture.Name, key);
        if (template is null) return fallback;
        return args is null ? template : LocaleFormatter.Format(template, culture, args);
    }

    /// <summary>The plural form of <paramref name="baseKey"/> for <paramref name="count"/>, with {count} filled in.</summary>
    public static string Plural(string baseKey, long count, object? args = null)
    {
        var culture = CultureInfo.CurrentUICulture;
        var template = Catalog.FindPlural(culture.Name, baseKey, count) ?? baseKey;
        var named = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (args is not null) foreach (var (k, v) in LocaleFormatter.ToNamed([args])) named[k] = v;
        named.TryAdd("count", count);
        return LocaleFormatter.Format(template, culture, named);
    }

    /// <summary>Switches the UI language until disposed, for text built outside a web request for a
    /// particular person (an email, a push notification). An unknown or empty culture means the
    /// server's default (<see cref="DefaultLanguage"/>), or English.</summary>
    public static IDisposable InLanguage(string? culture)
    {
        var previous = CultureInfo.CurrentUICulture;
        var matched = Catalog.Match(culture) ?? Catalog.Match(DefaultLanguage) ?? LocaleCatalog.English;
        try { CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(matched); }
        catch (CultureNotFoundException) { CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(LocaleCatalog.English); }
        return new Restore(previous);
    }

    private sealed class Restore(CultureInfo previous) : IDisposable
    {
        public void Dispose() => CultureInfo.CurrentUICulture = previous;
    }
}
