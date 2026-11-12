using System.Text.RegularExpressions;
using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

public class HelpContentServiceTests
{
    private readonly HelpContentService _help = new();

    [Fact]
    public void Topics_LoadFromEmbeddedMarkdown_InFileOrder()
    {
        Assert.NotEmpty(_help.Topics);
        Assert.Equal("overview", _help.Topics[0].Slug);
        Assert.Equal(_help.Topics.Count, _help.Topics.Select(t => t.Slug).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(_help.Topics, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Title));
            Assert.False(string.IsNullOrWhiteSpace(t.Summary));
            Assert.Contains("<h1", t.Html);
            // A bare "#anchor" would resolve against <base href="/"> and land on the Dashboard.
            Assert.DoesNotContain("href=\"#", t.Html);
        });
    }

    [Fact]
    public void Find_UnknownSlug_ReturnsNull() => Assert.Null(_help.Find("no-such-topic"));

    [Fact]
    public void Topics_OverviewAndInstallationFirst_ThenAlphabetical()
    {
        foreach (var culture in new[] { "en" }.Concat(_help.GetEmbeddedTranslations()))
        {
            var topics = _help.TopicsFor(culture);
            Assert.Equal(["overview", "installation"], topics.Take(2).Select(t => t.Slug));
            var rest = topics.Skip(2).Select(t => t.Title).ToList();
            var comparer = StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo(culture), ignoreCase: true);
            Assert.Equal(rest.OrderBy(t => t, comparer), rest);
        }
    }

    [Fact]
    public void English_IsEmbeddedFromHelpEnFolder()
    {
        Assert.True(_help.PageCount > 0);
        Assert.DoesNotContain("en", _help.GetEmbeddedTranslations(), StringComparer.OrdinalIgnoreCase);
    }

    private static HelpContentService WithPages(params (string Culture, string File, string Markdown)[] pages) =>
        new(null, pages.GroupBy(p => p.Culture).ToDictionary(g => g.Key,
            g => (IReadOnlyDictionary<string, string>)g.ToDictionary(p => p.File, p => p.Markdown)));

    [Fact]
    public void Translation_ReplacesPageFileByFile_AndKeepsSlugs()
    {
        var help = WithPages(
            ("en", "01-overview", "# Overview\n\nEnglish."),
            ("en", "02-cameras", "# Cameras\n\nEnglish."),
            ("es-AR", "01-overview", "# Resumen\n\nEspañol."));

        var topics = help.TopicsFor("es-AR");
        Assert.Equal(["overview", "cameras"], topics.Select(t => t.Slug));
        Assert.Equal("Resumen", topics[0].Title);
        Assert.Equal("Cameras", topics[1].Title); // untranslated page stays in English
        Assert.Equal(1, help.TranslatedPageCount("es-AR"));
        Assert.Equal(["es-AR"], help.GetEmbeddedTranslations());
    }

    [Fact]
    public void Translation_FallsBackToParentCulture_AndCultureWins()
    {
        var help = WithPages(
            ("en", "01-overview", "# Overview\n\nEnglish."),
            ("en", "02-cameras", "# Cameras\n\nEnglish."),
            ("es", "01-overview", "# Resumen (es)\n\nx"),
            ("es", "02-cameras", "# Cámaras (es)\n\nx"),
            ("es-AR", "01-overview", "# Resumen (AR)\n\nx"));

        var topics = help.TopicsFor("es-AR");
        Assert.Equal("Resumen (AR)", topics[0].Title);
        Assert.Equal("Cámaras (es)", topics[1].Title);
    }

    [Fact]
    public void RawHtmlInMarkdown_IsShownAsText()
    {
        var help = WithPages(("en", "01-overview", "# Overview\n\n<script>alert(1)</script>\n\n<img src=x onerror=alert(1)>"));
        var html = help.TopicsFor("en")[0].Html;
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<img", html);
    }

    [Fact]
    public void TranslatedHeadings_TakeEnglishIds_OnlyWhenStructureMatches()
    {
        const string en = "<h1 id=\"cameras\">Cameras</h1><h2 id=\"discovery\">Discovery</h2>";
        const string es = "<h1 id=\"cámaras\">Cámaras</h1><h2 id=\"descubrimiento\">Descubrimiento</h2><a href=\"#descubrimiento\">x</a>";
        var mapped = HelpContentService.WithEnglishHeadingIds(es, en);
        Assert.Contains("<h2 id=\"discovery\">Descubrimiento", mapped);
        Assert.Contains("href=\"#discovery\"", mapped);

        const string restructured = "<h1 id=\"cámaras\">Cámaras</h1><h3 id=\"descubrimiento\">Descubrimiento</h3>";
        Assert.Equal(restructured, HelpContentService.WithEnglishHeadingIds(restructured, en));
    }

    [Fact]
    public void CultureChain_ListsParentFirst() =>
        Assert.Equal(["es", "es-AR"], HelpContentService.CultureChain("es-AR"));

    /// <summary>Every /Help/{slug}#{anchor} link in the web UI and in the help pages themselves must
    /// point at a real topic and a real heading — a renamed heading would otherwise silently break
    /// the "Learn more" links scattered across the settings pages.</summary>
    [Fact]
    public void EveryHelpLink_ResolvesToTopicAndAnchor()
    {
        var webRoot = FindWebProjectDirectory();
        var files = Directory.EnumerateFiles(webRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".razor") || f.EndsWith(".cshtml") || f.EndsWith(".md"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            // Translated Help pages have translated headings, so their anchors differ; links are
            // checked against the English pages only.
            .Where(f => !IsTranslatedHelpPage(webRoot, f));

        var link = new Regex(@"/Help/(?<slug>[a-z0-9-]+)(#(?<anchor>[a-z0-9-]+))?");
        var broken = new List<string>();
        var checkedCount = 0;
        foreach (var file in files)
        {
            foreach (Match m in link.Matches(File.ReadAllText(file)))
            {
                checkedCount++;
                var topic = _help.Find(m.Groups["slug"].Value);
                if (topic is null)
                    broken.Add($"{Path.GetFileName(file)}: {m.Value} (no such topic)");
                else if (m.Groups["anchor"].Success && !topic.Html.Contains($"id=\"{m.Groups["anchor"].Value}\""))
                    broken.Add($"{Path.GetFileName(file)}: {m.Value} (no such heading)");
            }
        }

        Assert.True(checkedCount > 0, "No /Help links found — is the web project path right?");
        Assert.True(broken.Count == 0, "Broken Help links:" + Environment.NewLine + string.Join(Environment.NewLine, broken));
    }

    /// <summary>Same as above for each shipped translation: its pages' /Help links must reach a real
    /// topic and a heading as it reads in that language (translated headings get their own ids).</summary>
    [Fact]
    public void EveryHelpLink_InTranslations_ResolvesInThatLanguage()
    {
        var link = new Regex(@"/Help/(?<slug>[a-z0-9-]+)(#(?<anchor>[a-z0-9\p{L}-]+))?");
        var broken = new List<string>();
        foreach (var culture in _help.GetEmbeddedTranslations())
        {
            var topics = _help.TopicsFor(culture);
            foreach (var page in topics)
            foreach (Match m in link.Matches(page.Html))
            {
                var target = topics.FirstOrDefault(t => string.Equals(t.Slug, m.Groups["slug"].Value, StringComparison.OrdinalIgnoreCase));
                if (target is null)
                    broken.Add($"{culture}/{page.Slug}: {m.Value} (no such topic)");
                else if (m.Groups["anchor"].Success && !target.Html.Contains($"id=\"{m.Groups["anchor"].Value}\""))
                    broken.Add($"{culture}/{page.Slug}: {m.Value} (no such heading)");
            }
        }
        Assert.True(broken.Count == 0, "Broken Help links:" + Environment.NewLine + string.Join(Environment.NewLine, broken));
    }

    private static bool IsTranslatedHelpPage(string webRoot, string file)
    {
        var relative = Path.GetRelativePath(Path.Combine(webRoot, "Help"), file);
        if (relative.StartsWith("..", StringComparison.Ordinal)) return false;
        var culture = relative.Split(Path.DirectorySeparatorChar)[0];
        return !string.Equals(culture, "en", StringComparison.OrdinalIgnoreCase) && relative.Contains(Path.DirectorySeparatorChar);
    }

    private static string FindWebProjectDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LarisVMS.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "src", "LarisVMS.Web");
    }
}
