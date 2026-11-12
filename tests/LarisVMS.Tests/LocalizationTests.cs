using System.Globalization;
using System.Text.RegularExpressions;
using LarisVMS.Core.Localization;
using Microsoft.Extensions.Logging.Abstractions;

namespace LarisVMS.Tests;

/// <summary>Drop-in UI languages: file parsing, the lookup and plural fallbacks, placeholders, the
/// pseudo-locale, and that every key the UI asks for exists in the built-in English.</summary>
public sealed class LocalizationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "larisvms-locale-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _dropIn;
    private readonly string _shipped;

    private static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>
    {
        ["nav.live"] = "Live",
        ["nav.help"] = "Help",
        ["builds.reject"] = "Reject build {version}?",
        ["cameras.count_one"] = "{count} camera",
        ["cameras.count_other"] = "{count} cameras",
        ["js.exports.delete"] = "Delete {name}?",
        ["js.common.save"] = "Save"
    };

    public LocalizationTests()
    {
        _dropIn = Directory.CreateDirectory(Path.Combine(_root, "dropin")).FullName;
        _shipped = Directory.CreateDirectory(Path.Combine(_root, "shipped")).FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private LocaleCatalog Catalog() => new(_dropIn, _shipped, English, NullLogger.Instance, watch: false);

    [Fact]
    public void Parse_FlattensNestedObjects_AndReadsMeta()
    {
        var file = LocaleFile.Parse("fr", """{ "_meta": { "name": "Français", "author": "A" }, "nav": { "live": "En direct" }, "nav.help": "Aide" }""");
        Assert.Equal("fr", file.Culture);
        Assert.Equal("Français", file.Name);
        Assert.Equal("A", file.Author);
        Assert.Equal("En direct", file.Entries["nav.live"]);
        Assert.Equal("Aide", file.Entries["nav.help"]);
    }

    [Theory]
    [InlineData("template")]
    [InlineData("xx-ZZ")]
    [InlineData("../fr")]
    [InlineData("")]
    public void Parse_RejectsNamesThatArentKnownCultures(string culture)
    {
        Assert.Throws<FormatException>(() => LocaleFile.Parse(culture, "{}"));
    }

    [Fact]
    public void Parse_RejectsInvalidJson_WithAReadableMessage()
    {
        var ex = Assert.Throws<FormatException>(() => LocaleFile.Parse("fr", "{ \"a\": "));
        Assert.Contains("isn't valid JSON", ex.Message);
    }

    [Fact]
    public void Parse_CanonicalizesCultureCasing()
    {
        Assert.Equal("pt-BR", LocaleFile.Parse("pt-br", "{}").Culture);
    }

    [Fact]
    public void Find_FallsBackFromRegionToLanguageToEnglishToNull()
    {
        File.WriteAllText(Path.Combine(_shipped, "fr.json"), """{ "nav.live": "En direct" }""");
        var catalog = Catalog();

        Assert.Equal("En direct", catalog.Find("fr-CA", "nav.live"));
        Assert.Equal("Help", catalog.Find("fr-CA", "nav.help"));
        Assert.Null(catalog.Find("fr", "no.such.key"));
    }

    [Fact]
    public void DropIn_WinsKeyByKey_OverShipped()
    {
        File.WriteAllText(Path.Combine(_shipped, "de.json"), """{ "nav.live": "Live (shipped)", "nav.help": "Hilfe" }""");
        File.WriteAllText(Path.Combine(_dropIn, "de.json"), """{ "nav.live": "Live (drop-in)" }""");
        var catalog = Catalog();

        Assert.Equal("Live (drop-in)", catalog.Find("de", "nav.live"));
        Assert.Equal("Hilfe", catalog.Find("de", "nav.help"));
        Assert.Equal(LocaleSource.DropIn, catalog.Locales.Single(l => l.Culture == "de").Source);
    }

    [Fact]
    public void Match_PicksInstalledLanguage_OrNull()
    {
        File.WriteAllText(Path.Combine(_shipped, "fr.json"), """{ "nav.live": "En direct" }""");
        var catalog = Catalog();

        Assert.Equal("fr", catalog.Match("FR-ca"));
        Assert.Equal("en", catalog.Match("en-GB"));
        Assert.Null(catalog.Match("de"));
        Assert.Null(catalog.Match(null));
    }

    [Theory]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("zh-HK", "zh-Hant")]
    [InlineData("zh-MO", "zh-Hant")]
    [InlineData("zh-Hant-TW", "zh-Hant")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-SG", "zh-Hans")]
    [InlineData("zh", "zh-Hans")]
    [InlineData("zh-Hans-CN", "zh-Hans")]
    public void Match_ResolvesChineseRegionsToTheirScript(string requested, string expected)
    {
        File.WriteAllText(Path.Combine(_shipped, "zh-Hans.json"), """{ "nav.live": "实时" }""");
        File.WriteAllText(Path.Combine(_shipped, "zh-Hant.json"), """{ "nav.live": "即時" }""");
        Assert.Equal(expected, Catalog().Match(requested));
    }

    [Fact]
    public void Match_FallsBackToAnotherRegionOfTheSameLanguage_BeforeEnglish()
    {
        File.WriteAllText(Path.Combine(_shipped, "pt-BR.json"), """{ "nav.live": "Ao vivo" }""");
        File.WriteAllText(Path.Combine(_shipped, "es-AR.json"), """{ "nav.live": "En vivo" }""");
        var catalog = Catalog();

        Assert.Equal("pt-BR", catalog.Match("pt-PT"));
        Assert.Equal("pt-BR", catalog.Match("pt"));
        Assert.Equal("es-AR", catalog.Match("es-ES"));
        Assert.Null(catalog.Match("de-DE"));
        Assert.Null(catalog.Match("qps-plocm"));
    }

    [Fact]
    public void Parse_ReadsMachineTranslatedFlag()
    {
        Assert.True(LocaleFile.Parse("de", """{ "_meta": { "machineTranslated": true }, "nav.live": "Live" }""").MachineTranslated);
        Assert.False(LocaleFile.Parse("de", """{ "_meta": { "author": "A" }, "nav.live": "Live" }""").MachineTranslated);
    }

    /// <summary>Every language that ships with LarisVMS (src/LarisVMS.Web/locales) matches the
    /// built-in English: no keys English doesn't have, the same {placeholders} for each key, and
    /// script text (js.*) that the loader won't drop. A missing key only falls back to English, so
    /// coverage is reported by tools/locale-check.ps1 rather than failed here.</summary>
    [Fact]
    public void ShippedLanguages_MatchEnglish()
    {
        var english = LocaleCatalog.LoadEmbeddedEnglish();
        var englishBaseKeys = english.Keys.Select(LocaleFile.BaseKey).ToHashSet(StringComparer.Ordinal);
        var folder = Path.Combine(FindWebProjectDirectory(), "locales");
        var files = Directory.GetFiles(folder, "*.json");
        Assert.NotEmpty(files);

        var problems = new List<string>();
        foreach (var path in files)
        {
            var culture = Path.GetFileNameWithoutExtension(path);
            var json = File.ReadAllText(path);
            var parsed = LocaleFile.Parse(culture, json);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name == "_meta") continue;
                var key = prop.Name;
                var text = prop.Value.GetString() ?? "";
                if (!english.ContainsKey(key) && !englishBaseKeys.Contains(LocaleFile.BaseKey(key)))
                {
                    problems.Add($"{culture}: {key} isn't an English key");
                    continue;
                }
                if (key.StartsWith("js.", StringComparison.Ordinal) && !parsed.Entries.ContainsKey(key))
                    problems.Add($"{culture}: {key} has <, >, or a double quote, so it would show in English");
                var englishText = english.GetValueOrDefault(key) ?? english.GetValueOrDefault($"{LocaleFile.BaseKey(key)}_other");
                if (englishText is null) continue;
                var expected = LocaleFile.Placeholders(englishText);
                var actual = LocaleFile.Placeholders(text);
                // A plural form may leave {count} out ("one camera") or add it.
                if (!actual.Except(["count"]).ToHashSet().SetEquals(expected.Except(["count"])))
                    problems.Add($"{culture}: {key} has {{{string.Join("}, {", actual)}}}, English has {{{string.Join("}, {", expected)}}}");
            }
        }
        Assert.Empty(problems);
    }

    /// <summary>Translated Help folders (src/LarisVMS.Web/Help/{culture}) only hold pages English has.</summary>
    [Fact]
    public void ShippedHelpTranslations_OnlyHaveEnglishPages()
    {
        var help = Path.Combine(FindWebProjectDirectory(), "Help");
        var englishPages = Directory.GetFiles(Path.Combine(help, "en"), "*.md").Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extra = Directory.GetDirectories(help)
            .Where(d => !string.Equals(Path.GetFileName(d), "en", StringComparison.OrdinalIgnoreCase))
            .SelectMany(d => Directory.GetFiles(d, "*.md").Where(f => !englishPages.Contains(Path.GetFileName(f)))
                .Select(f => $"{Path.GetFileName(d)}/{Path.GetFileName(f)}"))
            .ToList();
        Assert.Empty(extra);
    }

    [Fact]
    public void BrokenFile_IsSkipped_AndReported()
    {
        File.WriteAllText(Path.Combine(_dropIn, "es.json"), "not json");
        File.WriteAllText(Path.Combine(_dropIn, "en.json"), """{ "nav.live": "Hijacked" }""");
        var catalog = Catalog();

        Assert.DoesNotContain(catalog.Locales, l => l.Culture == "es");
        Assert.Equal("Live", catalog.Find("en", "nav.live"));
        Assert.Equal(2, catalog.Problems.Count);
    }

    [Fact]
    public void Coverage_CountsPluralGroupsOnce()
    {
        // 6 base keys in English (cameras.count counts once); 3 translated.
        File.WriteAllText(Path.Combine(_shipped, "fr.json"),
            """{ "nav.live": "En direct", "cameras.count_one": "{count} caméra", "cameras.count_other": "{count} caméras", "nav.help": "Aide" }""");
        Assert.Equal(50, Catalog().Locales.Single(l => l.Culture == "fr").Coverage);
    }

    [Fact]
    public void Plural_UsesTheLanguagesOwnRules()
    {
        File.WriteAllText(Path.Combine(_shipped, "fr.json"), """{ "cameras.count_one": "{count} caméra", "cameras.count_other": "{count} caméras" }""");
        File.WriteAllText(Path.Combine(_shipped, "ru.json"), """{ "cameras.count_one": "{count} камера", "cameras.count_few": "{count} камеры", "cameras.count_many": "{count} камер" }""");
        var catalog = Catalog();

        // French treats 0 as singular; English doesn't.
        Assert.Equal("{count} caméra", catalog.FindPlural("fr", "cameras.count", 0));
        Assert.Equal("{count} cameras", catalog.FindPlural("en", "cameras.count", 0));
        Assert.Equal("{count} камеры", catalog.FindPlural("ru", "cameras.count", 3));
        Assert.Equal("{count} камер", catalog.FindPlural("ru", "cameras.count", 11));
        Assert.Equal("{count} камера", catalog.FindPlural("ru", "cameras.count", 21));
    }

    [Theory]
    [InlineData("en", 1, "one")]
    [InlineData("en", 0, "other")]
    [InlineData("fr", 1, "one")]
    [InlineData("pl", 22, "few")]
    [InlineData("pl", 25, "many")]
    [InlineData("ar", 0, "zero")]
    [InlineData("ar", 2, "two")]
    [InlineData("ar", 105, "few")]
    [InlineData("ja", 1, "other")]
    [InlineData("cs", 3, "few")]
    public void PluralRules_Categories(string language, long count, string expected)
    {
        Assert.Equal(expected, PluralRules.Category(language, count));
    }

    [Fact]
    public void Localizer_FillsNamedAndPositionalPlaceholders()
    {
        var localizer = new JsonStringLocalizer(Catalog());
        using var _ = new UiCulture("en");

        Assert.Equal("Reject build 1.2.3?", localizer["builds.reject", new { version = "1.2.3" }].Value);
        Assert.Equal("Reject build 1.2.3?", localizer["builds.reject", new { Version = "1.2.3" }].Value);
        Assert.Equal("2 cameras", localizer.Plural("cameras.count", 2).Value);
        Assert.Equal("1 camera", localizer.Plural("cameras.count", 1).Value);
    }

    [Fact]
    public void Localizer_MissingKey_ReturnsKey_AndMissingArgument_StaysVisible()
    {
        var localizer = new JsonStringLocalizer(Catalog());
        using var _ = new UiCulture("en");

        var missing = localizer["no.such.key"];
        Assert.True(missing.ResourceNotFound);
        Assert.Equal("no.such.key", missing.Value);
        Assert.Equal("Reject build {version}?", localizer["builds.reject", new { other = 1 }].Value);
    }

    [Fact]
    public void Formatter_FormatsNumbersForTheDisplayLanguage()
    {
        Assert.Equal("1234,5", LocaleFormatter.Format("{n}", CultureInfo.GetCultureInfo("fr-FR"), new { n = 1234.5 }));
        Assert.Equal("1234.5", LocaleFormatter.Format("{n}", CultureInfo.GetCultureInfo("en-US"), new { n = 1234.5 }));
    }

    [Fact]
    public void Resolve_ReturnsPrefixedKeys_WithEnglishFallback()
    {
        File.WriteAllText(Path.Combine(_shipped, "fr.json"), """{ "js.common.save": "Enregistrer", "nav.live": "En direct" }""");
        var resolved = Catalog().Resolve("fr-CA", "js.");

        Assert.Equal(2, resolved.Count);
        Assert.Equal("Enregistrer", resolved["js.common.save"]);
        Assert.Equal("Delete {name}?", resolved["js.exports.delete"]);
    }

    [Fact]
    public void Validate_RejectsBuiltIns_AndWarnsOnUnknownKeysAndPlaceholders()
    {
        var catalog = Catalog();
        Assert.Throws<FormatException>(() => catalog.Validate("en", """{ "nav.live": "x" }"""));
        Assert.Throws<FormatException>(() => catalog.Validate("qps-ploc", """{ "nav.live": "x" }"""));
        Assert.Throws<FormatException>(() => catalog.Validate("fr", "{}"));

        var warnings = catalog.Validate("fr", """{ "nav.live": "En direct", "nope": "x", "builds.reject": "Rejeter {versoin} ?", "cameras.count_few": "{count}" }""");
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("nope"));
        Assert.Contains(warnings, w => w.Contains("versoin"));
    }

    [Fact]
    public void PseudoLocale_KeepsPlaceholders_AndIsAlwaysAvailable()
    {
        var pseudo = PseudoLocale.Convert("Reject build {version}?");
        Assert.StartsWith("[", pseudo);
        Assert.EndsWith("]", pseudo);
        Assert.Contains("{version}", pseudo);
        Assert.DoesNotContain("Reject", pseudo);

        var catalog = Catalog();
        Assert.Equal(LocaleCatalog.Pseudo, catalog.Match("qps-ploc"));
        Assert.Contains("{count}", catalog.FindPlural(LocaleCatalog.Pseudo, "cameras.count", 2));
    }

    [Fact]
    public void Version_ChangesWhenAFileDoes()
    {
        var catalog = Catalog();
        var before = catalog.Version;
        File.WriteAllText(Path.Combine(_dropIn, "fr.json"), """{ "nav.live": "En direct" }""");
        catalog.Reload();
        Assert.NotEqual(before, catalog.Version);
    }

    [Fact]
    public void BuiltInEnglish_Loads_AndHasNoDuplicateOrEmptyText()
    {
        var english = LocaleCatalog.LoadEmbeddedEnglish();
        Assert.NotEmpty(english);
        Assert.All(english, e => Assert.False(string.IsNullOrWhiteSpace(e.Value), e.Key));
    }

    /// <summary>Every key the UI asks for (L["…"], L.Plural("…"), larisvmsI18n.t('…') / plural('…'))
    /// exists in the built-in English, so nothing shows up as a raw key.</summary>
    [Fact]
    public void EveryReferencedKey_ExistsInEnglish()
    {
        var english = LocaleCatalog.LoadEmbeddedEnglish();
        var baseKeys = english.Keys.Select(LocaleFile.BaseKey).ToHashSet(StringComparer.Ordinal);
        var src = Path.GetDirectoryName(FindWebProjectDirectory())!;
        // L["key"], L["key", args], L.Plural/Html/Markup/PluralHtml/PluralMarkup("key", …), Loc.T/Plural("key", …).
        var serverKey = new Regex(@"(?:\bL\[|\bL\.(?:Plural|Html|Markup|PluralHtml|PluralMarkup)\(|\bLoc\.(?:T|Plural)\()\s*""([a-z][A-Za-z0-9_.]*)""");
        // larisvmsI18n.t('js.key') / .plural('js.key', n), or the same through a local t()/plural().
        var scriptKey = new Regex(@"(?<![\w$])(?:larisvmsI18n\.)?(?:t|plural)\(\s*'(js\.[A-Za-z0-9_.]*)'");

        var missing = new List<string>();
        var referenced = 0;
        var projects = new[] { "LarisVMS.Web", "LarisVMS.Infrastructure", "LarisVMS.Core" };
        foreach (var path in projects.SelectMany(p => Directory.EnumerateFiles(Path.Combine(src, p), "*.*", SearchOption.AllDirectories)))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                path.Contains($"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}")) continue;
            var ext = Path.GetExtension(path);
            var regex = ext switch { ".cs" or ".cshtml" or ".razor" => serverKey, ".js" => scriptKey, _ => null };
            if (regex is null) continue;
            // Comment lines hold usage examples, not real lookups.
            var code = string.Join('\n', File.ReadLines(path).Where(l => !l.TrimStart().StartsWith("//")));
            foreach (Match m in regex.Matches(code))
            {
                referenced++;
                var key = m.Groups[1].Value;
                if (!english.ContainsKey(key) && !baseKeys.Contains(key))
                    missing.Add($"{Path.GetRelativePath(src, path)}: {key}");
            }
        }

        Assert.True(referenced > 0, "No localized text found — is the web project path right?");
        Assert.Empty(missing);
    }

    /// <summary>Every English key is used somewhere (as a "key" or 'key' literal), so translators
    /// aren't asked to translate text nobody sees.</summary>
    [Fact]
    public void EveryEnglishKey_IsUsed()
    {
        var src = Path.GetDirectoryName(FindWebProjectDirectory())!;
        var sep = Path.DirectorySeparatorChar;
        var code = string.Join('\n', new[] { "LarisVMS.Web", "LarisVMS.Infrastructure", "LarisVMS.Core" }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(src, p), "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".razor") || f.EndsWith(".cshtml") || f.EndsWith(".js"))
            .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}") && !f.Contains($"{sep}lib{sep}"))
            .Select(File.ReadAllText));

        // Looked up by a computed name: an object's class and category, and a color's name.
        string[] dynamicPrefixes = ["detection.class.", "detection.category.", "color."];
        var unused = LocaleCatalog.LoadEmbeddedEnglish().Keys
            .Select(LocaleFile.BaseKey).Distinct()
            .Where(k => !dynamicPrefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal)))
            .Where(k => !code.Contains($"\"{k}\"", StringComparison.Ordinal) && !code.Contains($"'{k}'", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(unused);
    }

    private static string FindWebProjectDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LarisVMS.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "src", "LarisVMS.Web");
    }

    private sealed class UiCulture : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentUICulture;
        public UiCulture(string name) => CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
        public void Dispose() => CultureInfo.CurrentUICulture = _previous;
    }
}
