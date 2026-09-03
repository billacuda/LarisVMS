using LarisVMS.Node;

namespace LarisVMS.Tests;

/// <summary>
/// Covers DetectionOrientation — the per-camera correction for a device that misreports the shape of
/// its detection stream (ONVIF advertising 704x480 for a stream it actually delivers as 480x704).
/// Pure arithmetic, same shape as DetectionModelSelectionTests.
///
/// The real-world numbers here are the Front Door / East Sideyard cameras that drove this: their Sub
/// profile advertises 704x480, which built landscape letterbox geometry and squashed a portrait frame
/// into it before the model ever saw it.
/// </summary>
public class DetectionOrientationTests
{
    [Fact]
    public void AutoLeavesTheReportedDimensionsAlone()
    {
        Assert.Equal((704, 480), DetectionOrientation.Apply(DetectionOrientation.Auto, 704, 480));
        Assert.Equal((480, 704), DetectionOrientation.Apply(DetectionOrientation.Auto, 480, 704));
    }

    [Fact]
    public void PortraitSwapsALandscapeReportedPair()
    {
        Assert.Equal((480, 704), DetectionOrientation.Apply(DetectionOrientation.Portrait, 704, 480));
    }

    [Fact]
    public void PortraitLeavesAnAlreadyPortraitPairAlone()
    {
        // Idempotent: a camera that reports its shape correctly and is *also* marked Portrait must not
        // be flipped into landscape by the setting.
        Assert.Equal((480, 704), DetectionOrientation.Apply(DetectionOrientation.Portrait, 480, 704));
    }

    [Fact]
    public void LandscapeSwapsAPortraitReportedPair()
    {
        Assert.Equal((704, 480), DetectionOrientation.Apply(DetectionOrientation.Landscape, 480, 704));
    }

    [Fact]
    public void LandscapeLeavesAnAlreadyLandscapePairAlone()
    {
        Assert.Equal((704, 480), DetectionOrientation.Apply(DetectionOrientation.Landscape, 704, 480));
    }

    [Fact]
    public void ASquareSourceIsNeverSwappedByEitherOrientation()
    {
        Assert.Equal((640, 640), DetectionOrientation.Apply(DetectionOrientation.Portrait, 640, 640));
        Assert.Equal((640, 640), DetectionOrientation.Apply(DetectionOrientation.Landscape, 640, 640));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Sideways")]
    public void AnUnrecognizedValueFallsBackToAutoRatherThanThrowing(string? orientation)
    {
        // A hand-edited Setting row, or a newer server talking to an older node — "unknown means
        // today's behavior", the same default DetectionModelSelection takes.
        Assert.Equal((704, 480), DetectionOrientation.Apply(orientation, 704, 480));
    }

    [Theory]
    [InlineData("portrait")]
    [InlineData("PORTRAIT")]
    public void MatchingIsCaseInsensitive(string orientation)
    {
        Assert.Equal((480, 704), DetectionOrientation.Apply(orientation, 704, 480));
    }

    [Fact]
    public void NonPositiveDimensionsPassThroughUntouched()
    {
        // Nothing sensible to orient, and swapping a zero would just move the problem — the caller's
        // own fallback chain is what handles a camera with no probed dimensions at all.
        Assert.Equal((0, 0), DetectionOrientation.Apply(DetectionOrientation.Portrait, 0, 0));
        Assert.Equal((-1, 480), DetectionOrientation.Apply(DetectionOrientation.Portrait, -1, 480));
    }
}
