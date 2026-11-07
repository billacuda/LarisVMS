using System.Globalization;
using Microsoft.Extensions.Localization;

namespace LarisVMS.Core.Localization;

/// <summary>
/// <see cref="IStringLocalizer"/> over <see cref="LocaleCatalog"/>, in the request's UI language
/// (<see cref="CultureInfo.CurrentUICulture"/>, set by <see cref="LocaleMiddleware"/>). Every type
/// shares one key space, so <c>IStringLocalizer&lt;Anything&gt;</c> and the plain
/// <c>IStringLocalizer</c> return the same text. A key nobody has written yet comes back as the key
/// itself, marked <see cref="LocalizedString.ResourceNotFound"/>.
/// </summary>
public sealed class JsonStringLocalizer(LocaleCatalog catalog) : IStringLocalizer
{
    public LocalizedString this[string name] => Get(name, []);

    public LocalizedString this[string name, params object[] arguments] => Get(name, arguments);

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
    {
        var culture = CultureInfo.CurrentUICulture.Name;
        foreach (var key in catalog.EnglishEntries.Keys)
            yield return new LocalizedString(key, catalog.Find(culture, key) ?? key, false);
    }

    /// <summary>The plural form of <paramref name="baseKey"/> for <paramref name="count"/>; {count}
    /// is filled in along with any other arguments.</summary>
    public LocalizedString Plural(string baseKey, long count, object? args = null)
    {
        var culture = CultureInfo.CurrentUICulture;
        var template = catalog.FindPlural(culture.Name, baseKey, count);
        if (template is null) return new LocalizedString(baseKey, baseKey, true);

        var named = args is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(LocaleFormatter.ToNamed([args]), StringComparer.OrdinalIgnoreCase);
        named.TryAdd("count", count);
        return new LocalizedString(baseKey, LocaleFormatter.Format(template, culture, named), false);
    }

    private LocalizedString Get(string name, object?[] arguments)
    {
        var culture = CultureInfo.CurrentUICulture;
        var template = catalog.Find(culture.Name, name);
        if (template is null) return new LocalizedString(name, name, true);
        return new LocalizedString(name, LocaleFormatter.Format(template, culture, arguments), false);
    }
}

/// <summary>Hands every caller the one shared <see cref="JsonStringLocalizer"/>.</summary>
public sealed class JsonStringLocalizerFactory(LocaleCatalog catalog) : IStringLocalizerFactory
{
    private readonly JsonStringLocalizer _localizer = new(catalog);

    public IStringLocalizer Create(Type resourceSource) => _localizer;

    public IStringLocalizer Create(string baseName, string location) => _localizer;
}

public static class StringLocalizerExtensions
{
    /// <summary>Plural text: <c>L.Plural("cameras.count", n)</c> picks cameras.count_one,
    /// cameras.count_other, and so on for the display language.</summary>
    public static LocalizedString Plural(this IStringLocalizer localizer, string baseKey, long count, object? args = null) =>
        localizer is JsonStringLocalizer json
            ? json.Plural(baseKey, count, args)
            : localizer[$"{baseKey}_other", args ?? new { count }];
}
