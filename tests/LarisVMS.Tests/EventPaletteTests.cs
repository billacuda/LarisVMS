using LarisVMS.Core;
using LarisVMS.Core.Enums;

namespace LarisVMS.Tests;

/// <summary>The admin-configurable event palette's resolution rules — what an unset slot falls back
/// to, and that a stored value is what actually gets drawn.</summary>
public class EventPaletteTests
{
    [Fact]
    public void AnEmptyPaletteResolvesEverythingToItsBuiltInDefault()
    {
        var palette = EventPalette.Empty;

        Assert.Equal(EventColors.DefaultMotion, palette.MotionColor);
        Assert.Equal(EventColors.DefaultRecording, palette.RecordingColor);
        Assert.All(DetectionDisplay.AllKinds,
            k => Assert.Equal(DetectionDisplay.ColorHex(k), palette.ColorFor(k)));
    }

    [Fact]
    public void AConfiguredColorWins()
    {
        var palette = new EventPalette("#111111", "#222222",
            new Dictionary<DetectionKind, string> { [DetectionKind.Animal] = "#333333" });

        Assert.Equal("#111111", palette.MotionColor);
        Assert.Equal("#222222", palette.RecordingColor);
        Assert.Equal("#333333", palette.ColorFor(DetectionKind.Animal));
        // Untouched classes still track their own defaults rather than inheriting anything.
        Assert.Equal(DetectionDisplay.ColorHex(DetectionKind.Human), palette.ColorFor(DetectionKind.Human));
    }

    [Fact]
    public void ABlankStoredValueIsTreatedAsUnset()
    {
        // Clearing a field writes "" rather than deleting the row, so this is the shape that reaches
        // the palette after an admin resets one colour back to the default.
        var palette = new EventPalette("", "",
            new Dictionary<DetectionKind, string> { [DetectionKind.Face] = "   " });

        Assert.Equal(EventColors.DefaultMotion, palette.MotionColor);
        Assert.Equal(EventColors.DefaultRecording, palette.RecordingColor);
        Assert.Equal(DetectionDisplay.ColorHex(DetectionKind.Face), palette.ColorFor(DetectionKind.Face));
    }

    [Theory]
    [InlineData("#28e070", "#28e070")]
    [InlineData("  #ABCDEF  ", "#ABCDEF")]
    [InlineData("red", null)]              // named colours aren't accepted
    [InlineData("javascript:alert(1)", null)]
    [InlineData("#fff; background:url(x)", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void OnlyHexColorsSurviveNormalization(string? input, string? expected)
    {
        // These end up in inline styles and canvas fill styles, so the same allowlist that guards
        // branding colours guards these.
        Assert.Equal(expected, EventColors.Normalize(input));
    }

    [Fact]
    public void SettingKeysAreNamedByClassNotNumber()
    {
        // Keying by the enum's number would let a renumbering silently repoint a colour at a
        // different class; the name can't drift that way.
        Assert.Equal("Timeline.Color.ObjectMissing", EventColors.DetectionKey(DetectionKind.ObjectMissing));
        Assert.All(DetectionDisplay.AllKinds,
            k => Assert.EndsWith(k.ToString(), EventColors.DetectionKey(k)));
    }
}
