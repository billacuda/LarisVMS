using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace LarisVMS.Core.Localization;

/// <summary>
/// Fills a text's {placeholders}. Arguments come either as one object whose properties name the
/// placeholders (<c>new { version, count }</c>) or a dictionary, or positionally ({0}, {1}, …).
/// Numbers and dates are formatted for the display language. A placeholder with no matching
/// argument is left as written, so a typo shows up instead of vanishing.
/// </summary>
public static class LocaleFormatter
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache = new();

    public static string Format(string template, CultureInfo culture, params object?[] args)
    {
        if (args.Length == 0 || template.IndexOf('{') < 0) return template;
        var named = ToNamed(args);

        var sb = new StringBuilder(template.Length + 16);
        var i = 0;
        while (i < template.Length)
        {
            var open = template.IndexOf('{', i);
            if (open < 0) { sb.Append(template, i, template.Length - i); break; }
            var close = template.IndexOf('}', open + 1);
            if (close < 0) { sb.Append(template, i, template.Length - i); break; }

            sb.Append(template, i, open - i);
            var name = template.Substring(open + 1, close - open - 1);
            if (named.TryGetValue(name, out var value))
                sb.Append(ToText(value, culture));
            else
                sb.Append(template, open, close - open + 1);
            i = close + 1;
        }
        return sb.ToString();
    }

    /// <summary>The argument list as name → value. One non-primitive argument is read by property
    /// (or dictionary key); anything else is positional.</summary>
    public static IReadOnlyDictionary<string, object?> ToNamed(object?[] args)
    {
        var named = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (args is [{ } single] && !IsScalar(single))
        {
            if (single is IDictionary dict)
            {
                foreach (DictionaryEntry e in dict)
                    if (e.Key?.ToString() is { } k) named[k] = e.Value;
            }
            else if (single is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var (k, v) in pairs) named[k] = v;
            }
            else
            {
                foreach (var p in PropertyCache.GetOrAdd(single.GetType(),
                             t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0).ToArray()))
                    named[p.Name] = p.GetValue(single);
            }
            return named;
        }
        for (var n = 0; n < args.Length; n++) named[n.ToString(CultureInfo.InvariantCulture)] = args[n];
        return named;
    }

    private static bool IsScalar(object value) =>
        value is string or IFormattable or bool or char || value.GetType().IsPrimitive || value.GetType().IsEnum;

    private static string ToText(object? value, CultureInfo culture) => value switch
    {
        null => "",
        string s => s,
        IFormattable f => f.ToString(null, culture),
        _ => value.ToString() ?? ""
    };
}
