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
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

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
        Assert.Empty(broken);
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
