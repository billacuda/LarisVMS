using LarisVMS.Core.Enums;
using LarisVMS.Node;

namespace LarisVMS.Tests;

public class DetectionModelSelectionTests
{
    [Fact]
    public void AutoOnNvidiaChoosesDFine()
    {
        Assert.Equal(DetectionModelFamily.DFine, DetectionModelSelection.Choose(DetectionModelFamily.Auto, AiAccelerator.Nvidia));
    }

    [Theory]
    [InlineData(AiAccelerator.Intel)]
    [InlineData(AiAccelerator.Amd)]
    [InlineData(AiAccelerator.Cpu)]
    public void AutoOnNonNvidiaFallsBackToDFineBecauseYoloXIsNotImplementedYet(AiAccelerator accelerator)
    {
        // The "ideal" Auto default for these is YOLOX, but it doesn't exist yet — Choose must
        // substitute D-FINE rather than return a family DetectionEngineFactory would throw for.
        Assert.Equal(DetectionModelFamily.DFine, DetectionModelSelection.Choose(DetectionModelFamily.Auto, accelerator));
    }

    [Fact]
    public void ExplicitDFineChoiceIsAlwaysHonored()
    {
        Assert.Equal(DetectionModelFamily.DFine, DetectionModelSelection.Choose(DetectionModelFamily.DFine, AiAccelerator.Intel));
    }

    [Theory]
    [InlineData(DetectionModelFamily.RfDetr)]
    [InlineData(DetectionModelFamily.YoloX)]
    public void AnExplicitUnimplementedChoiceAlsoFallsBackToDFine(DetectionModelFamily unimplemented)
    {
        // Not just Auto's own default — an explicit (stale setting, hand-edited DB row) choice of
        // an unimplemented family must not crash the pipeline either.
        Assert.Equal(DetectionModelFamily.DFine, DetectionModelSelection.Choose(unimplemented, AiAccelerator.Nvidia));
    }
}
