using LarisVMS.Core;

namespace LarisVMS.Tests;

/// <summary>Branding values are interpolated into a &lt;style&gt; block and an img src on every page,
/// so validation is the security boundary for this feature — anything that gets past these helpers
/// reaches every user's browser. Everything is allowlist-based: colors must be hex literals, fonts
/// resolve through a fixed table, and logos must be base64 image data URIs.</summary>
public class BrandingTests
{
    [Theory]
    [InlineData("#fff")]
    [InlineData("#FFF")]
    [InlineData("#0d6efd")]
    [InlineData("#0D6EFD")]
    [InlineData("  #0d6efd  ")] // trimmed
    public void AcceptsHexColors(string value)
    {
        Assert.Equal(value.Trim(), Branding.NormalizeColor(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("red")]                              // named colors: not accepted, keeps the grammar strict
    [InlineData("rgb(255,0,0)")]
    [InlineData("var(--x)")]
    [InlineData("#12345")]                           // wrong length
    [InlineData("#gggggg")]                          // not hex
    [InlineData("#fff; } body { display:none")]      // the injection this guards against
    [InlineData("#fff\"></style><script>alert(1)</script>")]
    public void RejectsAnythingThatIsNotAHexColor(string? value)
    {
        Assert.Null(Branding.NormalizeColor(value));
    }

    [Fact]
    public void ResolvesAKnownFontKeyToThisCodebasesOwnStack()
    {
        var stack = Branding.FontStackFor("georgia");

        Assert.NotNull(stack);
        Assert.Equal(Branding.FontStacks["georgia"], stack);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-font")]
    [InlineData("Arial; } body { display:none")]
    public void AnUnknownFontKeyResolvesToNoStackAtAll(string? value)
    {
        // The stored value is only ever a key — the CSS stack comes from FontStacks, so an unknown
        // or hostile key can't put anything into the stylesheet.
        Assert.Null(Branding.NormalizeFontKey(value));
        Assert.Null(Branding.FontStackFor(value));
    }

    [Fact]
    public void EveryOfferedFontChoiceActuallyResolves()
    {
        // Guards against a picker option whose key isn't in the stack table — it would silently
        // save and then do nothing.
        Assert.All(Branding.FontChoices, choice =>
        {
            Assert.Equal(choice.Key, Branding.NormalizeFontKey(choice.Key));
            Assert.NotNull(Branding.FontStackFor(choice.Key));
        });
    }

    /// <summary>A 1x1 transparent GIF — small, real, and actually base64-decodable.</summary>
    private const string TinyGif = "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7";

    [Fact]
    public void AcceptsARealImageDataUri()
    {
        Assert.Equal(TinyGif, Branding.NormalizeLogoDataUri(TinyGif));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com/logo.png")]                    // remote URLs: not stored, not fetched
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==")]
    [InlineData("data:image/png;base64,!!!not base64!!!")]
    public void RejectsAnythingThatIsNotAnImageDataUri(string? value)
    {
        Assert.Null(Branding.NormalizeLogoDataUri(value));
    }

    [Fact]
    public void RejectsSvgEvenThoughItIsAnImageType()
    {
        // SVG can carry script and this string goes straight into an img src on every page.
        var svg = "data:image/svg+xml;base64,PHN2Zy8+";

        Assert.Null(Branding.NormalizeLogoDataUri(svg));
    }

    [Fact]
    public void RejectsAnOversizedLogo()
    {
        var huge = "data:image/png;base64," + new string('A', Branding.MaxLogoDataUriLength);

        Assert.Null(Branding.NormalizeLogoDataUri(huge));
    }

    [Fact]
    public void RejectsBase64ThatCannotActuallyDecode()
    {
        // Passes the character-class check but is not a valid base64 length — would render as a
        // broken image on every page if it were stored.
        var malformed = "data:image/png;base64,QUJDR";

        Assert.Null(Branding.NormalizeLogoDataUri(malformed));
    }

    [Theory]
    [InlineData(null, BrandingOptions.DefaultAppName)]
    [InlineData("", BrandingOptions.DefaultAppName)]
    [InlineData("   ", BrandingOptions.DefaultAppName)]
    [InlineData("  Acme Security  ", "Acme Security")]
    public void AppNameFallsBackToTheDefaultWhenBlank(string? value, string expected)
    {
        Assert.Equal(expected, Branding.NormalizeAppName(value));
    }

    [Fact]
    public void DefaultOptionsExposeNoStylingAtAll()
    {
        // A fresh install must render exactly as it did before branding existed.
        Assert.Equal(BrandingOptions.DefaultAppName, BrandingOptions.Default.AppName);
        Assert.Null(BrandingOptions.Default.PrimaryColor);
        Assert.Null(BrandingOptions.Default.AccentColor);
        Assert.Null(BrandingOptions.Default.LogoDataUri);
        Assert.Null(BrandingOptions.Default.FontStack);
    }
}
