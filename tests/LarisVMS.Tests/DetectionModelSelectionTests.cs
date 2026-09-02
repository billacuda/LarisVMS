using LarisVMS.Core.Enums;
using LarisVMS.Node;

namespace LarisVMS.Tests;

public class DetectionModelSelectionTests
{
    [Theory]
    [InlineData(AiAccelerator.Nvidia)]
    [InlineData(AiAccelerator.Intel)]
    [InlineData(AiAccelerator.Amd)]
    [InlineData(AiAccelerator.Cpu)]
    public void AutoResolvesToYoloXOnEveryAccelerator(AiAccelerator accelerator)
    {
        Assert.Equal(DetectionModelFamily.YoloX, DetectionModelSelection.Choose(DetectionModelFamily.Auto, accelerator));
    }

    [Fact]
    public void ExplicitDFineChoiceIsAlwaysHonored()
    {
        Assert.Equal(DetectionModelFamily.DFine, DetectionModelSelection.Choose(DetectionModelFamily.DFine, AiAccelerator.Intel));
    }

    [Fact]
    public void ExplicitYoloXChoiceIsHonored()
    {
        Assert.Equal(DetectionModelFamily.YoloX, DetectionModelSelection.Choose(DetectionModelFamily.YoloX, AiAccelerator.Nvidia));
    }

    [Fact]
    public void RfDetrStillFallsBackToDFine()
    {
        // RF-DETR has no decoder yet — Choose must substitute D-FINE rather than return a family
        // DetectionEngineFactory would throw for.
        Assert.Equal(DetectionModelFamily.DFine, DetectionModelSelection.Choose(DetectionModelFamily.RfDetr, AiAccelerator.Nvidia));
    }
}
