using LarisVMS.Core.Localization;
using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

/// <summary>
/// Text from language files and translated Help pages can't add script, attributes, or off-site
/// links to a page: script text with markup or a double quote is dropped (it shows in English), and
/// Help Markdown keeps only web, mail, and in-app links, with no {attribute} blocks.
/// </summary>
public class TranslatedContentSafetyTests
{
    [Fact]
    public void ScriptTextWithMarkupOrADoubleQuoteIsDropped()
    {
        var file = LocaleFile.Parse("fr", """
            {
              "js.ok": "Supprimer “{name}” ?",
              "js.tag": "<img src=x onerror=alert(1)>",
              "js.quote": "x\" onmouseover=\"alert(1)",
              "nav.live": "<b>En direct</b>"
            }
            """);

        Assert.True(file.Entries.ContainsKey("js.ok"));
        Assert.False(file.Entries.ContainsKey("js.tag"));
        Assert.False(file.Entries.ContainsKey("js.quote"));
        Assert.True(file.Entries.ContainsKey("nav.live")); // server text is encoded where it's shown
    }

    [Fact]
    public void TheBuiltInEnglishScriptTextIsAllPlain() =>
        Assert.All(LocaleCatalog.LoadEmbeddedEnglish().Where(e => e.Key.StartsWith("js.", StringComparison.Ordinal)),
            e => Assert.True(LocaleFile.IsPlainScriptText(e.Value), e.Key));

    [Theory]
    [InlineData("https://www.larisvms.com/docs/", true)]
    [InlineData("http://example.com", true)]
    [InlineData("mailto:someone@example.com", true)]
    [InlineData("/Help/nodes#server-certificate", true)]
    [InlineData("#allow-lists", true)]
    [InlineData("installation", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData(" JavaScript:alert(1)", false)]
    [InlineData("data:text/html,<script>alert(1)</script>", false)]
    [InlineData("vbscript:msgbox", false)]
    [InlineData("//evil.example/", false)]
    [InlineData("/\\evil.example/", false)]
    public void HelpLinksOnlyGoToWebMailOrInAppAddresses(string url, bool safe) =>
        Assert.Equal(safe, HelpContentService.IsSafeUrl(url));

    [Fact]
    public void HelpMarkdownCantAddScriptLinksOrAttributes()
    {
        var html = HelpContentService.Render("""
            # Title {onmouseover="alert(1)" style="color:red"}

            [click](javascript:alert(1)) and [docs](/Help/nodes) and <script>alert(1)</script>
            """);

        Assert.DoesNotContain("javascript:", html);
        // The braces stay as text (at most in the heading's id); they never become attributes.
        Assert.DoesNotMatch(@"<[^>]*\sonmouseover=", html);
        Assert.DoesNotMatch(@"<[^>]*\sstyle=", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("href=\"/Help/nodes\"", html);
    }
}
