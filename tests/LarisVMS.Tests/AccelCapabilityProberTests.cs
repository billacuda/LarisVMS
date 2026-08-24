using LarisVMS.Core.Enums;
using LarisVMS.Node;

namespace LarisVMS.Tests;

public class AccelCapabilityProberTests
{
    [Fact]
    public void DetectsNvidiaFromAVideoControllerName()
    {
        var result = AccelCapabilityProber.ParseAccelerators(["NVIDIA GeForce RTX 4080 SUPER"]);

        Assert.Equal([AiAccelerator.Nvidia], result);
    }

    [Fact]
    public void DetectsIntelFromAVideoControllerName()
    {
        var result = AccelCapabilityProber.ParseAccelerators(["Intel(R) UHD Graphics 770"]);

        Assert.Equal([AiAccelerator.Intel], result);
    }

    [Theory]
    [InlineData("AMD Radeon RX 7900 XTX")]
    [InlineData("Radeon(TM) Graphics")]
    public void DetectsAmdFromAVideoControllerName(string name)
    {
        var result = AccelCapabilityProber.ParseAccelerators([name]);

        Assert.Equal([AiAccelerator.Amd], result);
    }

    [Fact]
    public void DetectsMultipleVendorsWhenBothAreInstalled()
    {
        // A common real-world case: a discrete NVIDIA card alongside an Intel iGPU on the same
        // motherboard, both enumerated as separate video controllers.
        var result = AccelCapabilityProber.ParseAccelerators(["Intel(R) UHD Graphics 770", "NVIDIA GeForce RTX 2080"]);

        Assert.Contains(AiAccelerator.Nvidia, result);
        Assert.Contains(AiAccelerator.Intel, result);
        Assert.DoesNotContain(AiAccelerator.Amd, result);
    }

    [Fact]
    public void AnUnrecognizedVendorDetectsNothing()
    {
        var result = AccelCapabilityProber.ParseAccelerators(["Microsoft Basic Display Adapter"]);

        Assert.Empty(result);
    }

    [Fact]
    public void EmptyInputDetectsNothing()
    {
        var result = AccelCapabilityProber.ParseAccelerators([]);

        Assert.Empty(result);
    }

    [Fact]
    public void IsCaseInsensitive()
    {
        var result = AccelCapabilityProber.ParseAccelerators(["nvidia geforce gtx 1080"]);

        Assert.Equal([AiAccelerator.Nvidia], result);
    }
}

public class AccelSelectionTests
{
    [Fact]
    public void ExplicitCpuIsAlwaysHonoredRegardlessOfDetectedHardware()
    {
        var result = AccelSelection.Choose(AiAccelerator.Cpu, []);

        Assert.Equal(AiAccelerator.Cpu, result);
    }

    [Fact]
    public void AutoResolvesToNvidiaWhenDetected()
    {
        var result = AccelSelection.Choose(AiAccelerator.Auto, [AiAccelerator.Nvidia, AiAccelerator.Intel]);

        Assert.Equal(AiAccelerator.Nvidia, result);
    }

    [Fact]
    public void AutoPrefersNvidiaOverIntelOverAmd()
    {
        Assert.Equal(AiAccelerator.Nvidia, AccelSelection.Choose(AiAccelerator.Auto, [AiAccelerator.Amd, AiAccelerator.Intel, AiAccelerator.Nvidia]));
        Assert.Equal(AiAccelerator.Intel, AccelSelection.Choose(AiAccelerator.Auto, [AiAccelerator.Amd, AiAccelerator.Intel]));
        Assert.Equal(AiAccelerator.Amd, AccelSelection.Choose(AiAccelerator.Auto, [AiAccelerator.Amd]));
    }

    [Fact]
    public void AutoNeverFallsBackToCpuWhenNothingIsDetected()
    {
        var result = AccelSelection.Choose(AiAccelerator.Auto, []);

        Assert.Null(result);
    }

    [Fact]
    public void AnExplicitChoiceIsHonoredWhenItsHardwareIsPresent()
    {
        var result = AccelSelection.Choose(AiAccelerator.Intel, [AiAccelerator.Nvidia, AiAccelerator.Intel]);

        Assert.Equal(AiAccelerator.Intel, result);
    }

    [Fact]
    public void AnExplicitChoiceResolvesToNullWhenItsHardwareIsAbsent()
    {
        // Forcing Nvidia on a machine with no NVIDIA GPU — must not silently fall back to
        // something else; NodeWorker's own handling of a null result is what logs this clearly and
        // leaves every other detection source for the camera untouched.
        var result = AccelSelection.Choose(AiAccelerator.Nvidia, [AiAccelerator.Intel]);

        Assert.Null(result);
    }
}
