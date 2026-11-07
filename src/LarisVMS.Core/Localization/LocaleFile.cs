using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LarisVMS.Core.Localization;

/// <summary>
/// One language file: a JSON object of key → text, with an optional "_meta" object (name, author,
/// baseVersion). Nested objects are flattened with dots, so <c>{"nav": {"live": "Live"}}</c> and
/// <c>{"nav.live": "Live"}</c> mean the same thing.
/// </summary>
public sealed partial record LocaleFile(string Culture, IReadOnlyDictionary<string, string> Entries,
    string? Name, string? Author, string? BaseVersion)
{
    /// <summary>Upper bound for an uploaded or dropped-in file: English is a few hundred KB.</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    private static readonly string[] PluralSuffixes =
        ["_zero", "_one", "_two", "_few", "_many", "_other"];

    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]*|\d+)\}")]
    private static partial Regex PlaceholderRegex();

    /// <summary>Parses <paramref name="json"/>. Throws <see cref="FormatException"/> with a message
    /// fit to show an admin when the file isn't usable.</summary>
    public static LocaleFile Parse(string culture, string json)
    {
        if (!IsValidCultureName(culture))
            throw new FormatException($"\"{culture}\" isn't a language code this server recognizes (for example fr, de, or pt-BR).");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            throw new FormatException($"The file isn't valid JSON (line {ex.LineNumber + 1}): {ex.Message}");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new FormatException("The file must be a JSON object of \"key\": \"text\" pairs.");

            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            string? name = null, author = null, baseVersion = null;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name == "_meta")
                {
                    if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                    name = ReadString(prop.Value, "name");
                    author = ReadString(prop.Value, "author");
                    baseVersion = ReadString(prop.Value, "baseVersion");
                    continue;
                }
                Flatten(prop.Name, prop.Value, entries);
            }
            return new LocaleFile(CanonicalName(culture), entries, name, author, baseVersion);
        }
    }

    /// <summary>The {placeholders} in a text, by name.</summary>
    public static IReadOnlySet<string> Placeholders(string text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in PlaceholderRegex().Matches(text)) set.Add(m.Groups[1].Value);
        return set;
    }

    /// <summary>"cameras.count_one" → "cameras.count"; a key without a plural suffix is returned as is.</summary>
    public static string BaseKey(string key)
    {
        var suffix = PluralSuffixes.FirstOrDefault(s => key.EndsWith(s, StringComparison.Ordinal));
        return suffix is null ? key : key[..^suffix.Length];
    }

    public static bool IsValidCultureName(string culture)
    {
        if (string.IsNullOrWhiteSpace(culture) || culture.Length > 20) return false;
        if (!culture.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return false;
        // GetCultureInfo accepts any well-formed tag ("template", "xx-ZZ"), so only the cultures
        // .NET actually has data for count.
        return KnownCultures.Value.Contains(culture);
    }

    private static readonly Lazy<HashSet<string>> KnownCultures = new(() =>
        CultureInfo.GetCultures(CultureTypes.AllCultures)
            .Select(c => c.Name).Where(n => n.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>"pt-br" → "pt-BR": the casing .NET and browsers use.</summary>
    public static string CanonicalName(string culture) => CultureInfo.GetCultureInfo(culture).Name;

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static void Flatten(string prefix, JsonElement value, Dictionary<string, string> into)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var text = value.GetString();
                if (!string.IsNullOrEmpty(text)) into[prefix] = text;
                break;
            case JsonValueKind.Object:
                foreach (var child in value.EnumerateObject())
                    Flatten($"{prefix}.{child.Name}", child.Value, into);
                break;
            // Numbers, arrays, null: not text, ignored rather than failing the whole file.
        }
    }
}
