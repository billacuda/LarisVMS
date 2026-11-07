namespace LarisVMS.Core.Localization;

/// <summary>
/// CLDR plural categories (zero, one, two, few, many, other) for whole-number counts, for the
/// languages translators are most likely to drop in. A language not listed here gets "other" for
/// every count, which still reads correctly as long as its file has an "_other" form for each
/// plural key (the English fallback always does).
/// </summary>
public static class PluralRules
{
    public const string Zero = "zero", One = "one", Two = "two", Few = "few", Many = "many", Other = "other";

    public static string Category(string language, long count)
    {
        var n = Math.Abs(count);
        var lang = language.Split('-')[0].ToLowerInvariant();
        switch (lang)
        {
            // No plural forms.
            case "ja": case "zh": case "ko": case "th": case "vi": case "id": case "ms":
                return Other;

            // 0 and 1 are singular.
            case "fr": case "pt": case "hy": case "kab":
                return n is 0 or 1 ? One : Other;

            // East Slavic.
            case "ru": case "uk": case "be":
                if (n % 10 == 1 && n % 100 != 11) return One;
                if (n % 10 is >= 2 and <= 4 && n % 100 is not (>= 12 and <= 14)) return Few;
                return Many;

            case "pl":
                if (n == 1) return One;
                if (n % 10 is >= 2 and <= 4 && n % 100 is not (>= 12 and <= 14)) return Few;
                return Many;

            case "cs": case "sk":
                if (n == 1) return One;
                return n is >= 2 and <= 4 ? Few : Other;

            case "ro":
                if (n == 1) return One;
                return n == 0 || n % 100 is >= 2 and <= 19 ? Few : Other;

            case "lt":
                if (n % 10 == 1 && n % 100 is not (>= 11 and <= 19)) return One;
                if (n % 10 is >= 2 and <= 9 && n % 100 is not (>= 11 and <= 19)) return Few;
                return Other;

            case "lv":
                if (n % 10 == 0 || n % 100 is >= 11 and <= 19) return Zero;
                return n % 10 == 1 && n % 100 != 11 ? One : Other;

            case "hr": case "sr": case "bs":
                if (n % 10 == 1 && n % 100 != 11) return One;
                if (n % 10 is >= 2 and <= 4 && n % 100 is not (>= 12 and <= 14)) return Few;
                return Other;

            case "sl":
                if (n % 100 == 1) return One;
                if (n % 100 == 2) return Two;
                return n % 100 is 3 or 4 ? Few : Other;

            case "he":
                if (n == 1) return One;
                return n == 2 ? Two : Other;

            case "ar":
                if (n == 0) return Zero;
                if (n == 1) return One;
                if (n == 2) return Two;
                if (n % 100 is >= 3 and <= 10) return Few;
                return n % 100 is >= 11 and <= 99 ? Many : Other;

            case "ga":
                if (n == 1) return One;
                if (n == 2) return Two;
                if (n is >= 3 and <= 6) return Few;
                return n is >= 7 and <= 10 ? Many : Other;

            case "cy":
                return n switch { 0 => Zero, 1 => One, 2 => Two, 3 => Few, 6 => Many, _ => Other };

            // English, German, Dutch, Scandinavian, Spanish, Italian, Greek, Hungarian, Turkish,
            // Finnish, Estonian, Bulgarian, and most others: one vs other.
            default:
                return n == 1 ? One : Other;
        }
    }
}
