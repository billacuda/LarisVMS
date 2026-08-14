using LarisVMS.Core;

namespace LarisVMS.Tests;

/// <summary>Covers CameraEventClassifier.IsMotionActive — the M8 pass 6 rule deciding whether one
/// ONVIF PullPoint notification means "motion is presently active," the only thing NodeWorker's
/// CameraEventSession needs to feed MotionHysteresis correctly.</summary>
public class CameraEventClassifierTests
{
    [Theory]
    [InlineData("tns1:RuleEngine/CellMotionDetector/Motion")]
    [InlineData("tns1:VideoSource/MotionAlarm")]
    [InlineData("tns1:RuleEngine/MotionRegionDetector/Motion")]
    public void RecognizedMotionTopicWithStateTrueIsActive(string topic)
    {
        Assert.True(CameraEventClassifier.IsMotionActive(topic, new Dictionary<string, string> { ["State"] = "true" }));
    }

    [Fact]
    public void RecognizedMotionTopicWithStateFalseIsNotActive()
    {
        // The same topic fires on both the rising AND falling edge — this is the case that makes
        // topic-matching alone insufficient; the state item is what actually distinguishes them.
        Assert.False(CameraEventClassifier.IsMotionActive(
            "tns1:RuleEngine/CellMotionDetector/Motion", new Dictionary<string, string> { ["State"] = "false" }));
    }

    [Fact]
    public void IsMotionItemNameIsAlsoRecognizedForVideoSourceMotionAlarm()
    {
        Assert.True(CameraEventClassifier.IsMotionActive(
            "tns1:VideoSource/MotionAlarm", new Dictionary<string, string> { ["IsMotion"] = "true" }));
    }

    [Fact]
    public void UnrecognizedTopicIsNeverMotionRegardlessOfStateItem()
    {
        Assert.False(CameraEventClassifier.IsMotionActive(
            "tns1:Device/Trigger/DigitalInput", new Dictionary<string, string> { ["State"] = "true" }));
    }

    [Fact]
    public void MissingStateItemIsNotActive()
    {
        // Never manufacture motion from a payload that doesn't actually confirm it.
        Assert.False(CameraEventClassifier.IsMotionActive(
            "tns1:RuleEngine/CellMotionDetector/Motion", new Dictionary<string, string>()));
    }

    [Fact]
    public void UnparseableStateValueIsNotActive()
    {
        Assert.False(CameraEventClassifier.IsMotionActive(
            "tns1:RuleEngine/CellMotionDetector/Motion", new Dictionary<string, string> { ["State"] = "not-a-bool" }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyOrNullTopicIsNeverMotion(string? topic)
    {
        Assert.False(CameraEventClassifier.IsMotionActive(topic, new Dictionary<string, string> { ["State"] = "true" }));
    }

    // M8 pass 8: TryGetBooleanState is IsMotionActive's own state-item lookup, pulled out and made
    // public so CameraEventSession can reuse it for a toggle-mode EventTagRule's arbitrary (not
    // necessarily motion-family) single topic — these cover it directly rather than only indirectly
    // through IsMotionActive's topic-gated tests above.
    [Fact]
    public void TryGetBooleanStateReadsStateItem()
    {
        Assert.True(CameraEventClassifier.TryGetBooleanState(new Dictionary<string, string> { ["State"] = "true" }));
        Assert.False(CameraEventClassifier.TryGetBooleanState(new Dictionary<string, string> { ["State"] = "false" }));
    }

    [Fact]
    public void TryGetBooleanStateReadsMotionItemWhenStateIsAbsent()
    {
        Assert.True(CameraEventClassifier.TryGetBooleanState(new Dictionary<string, string> { ["Motion"] = "true" }));
    }

    [Fact]
    public void TryGetBooleanStateWithNoRecognizedItemIsFalse()
    {
        Assert.False(CameraEventClassifier.TryGetBooleanState(new Dictionary<string, string> { ["SomethingElse"] = "true" }));
    }
}
