using LarisVMS.Core;
using LarisVMS.Core.Enums;

namespace LarisVMS.Tests;

/// <summary>Covers object-detection classification — deciding which class (if any) an ONVIF
/// PullPoint notification is reporting, and whether that class is presently being seen. Topic
/// spellings here are the real vendor shapes this has to cope with: there is no single standard
/// name, so matching is by substring within the vendor's own topic string.</summary>
public class CameraEventDetectionClassifierTests
{
    private static Dictionary<string, string> Items(params (string Key, string Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value);

    [Theory]
    [InlineData("tns1:RuleEngine/PeopleDetector/People", DetectionKind.Human)]
    [InlineData("tns1:RuleEngine/MyRuleDetector/PersonDetector", DetectionKind.Human)]
    [InlineData("tns1:RuleEngine/FieldDetector/HumanShapeDetect", DetectionKind.Human)]
    [InlineData("tns1:RuleEngine/VehicleDetector/Vehicle", DetectionKind.Vehicle)]
    [InlineData("tns1:RuleEngine/CarDetector/Car", DetectionKind.Vehicle)]
    [InlineData("tns1:RuleEngine/FaceDetector/Face", DetectionKind.Face)]
    [InlineData("tns1:RuleEngine/ObjectDetector/Object", DetectionKind.Other)]
    public void RecognizesEachVendorTopicShape(string topic, DetectionKind expected)
    {
        Assert.Equal(expected, CameraEventClassifier.DetectionTopicKind(topic));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tns1:RuleEngine/CellMotionDetector/Motion")]   // plain motion, not an object class
    [InlineData("tns1:VideoSource/MotionAlarm")]
    [InlineData("tns1:Device/HardwareFailure/StorageFailure")]
    public void IgnoresTopicsThatAreNotObjectDetection(string? topic)
    {
        // Anything unrecognized must stay unclassified — it still flows to the raw CameraEvents log
        // exactly as before, so an unknown topic can never be a regression.
        Assert.Null(CameraEventClassifier.DetectionTopicKind(topic));
    }

    [Fact]
    public void MatchesTopicsCaseInsensitively()
    {
        Assert.Equal(DetectionKind.Human, CameraEventClassifier.DetectionTopicKind("tns1:ruleengine/peopledetector/people"));
    }

    [Fact]
    public void FaceIsNotSwallowedByABroaderMarker()
    {
        // Ordering guard: a topic containing both a specific and a generic marker must resolve to
        // the specific one.
        Assert.Equal(DetectionKind.Face,
            CameraEventClassifier.DetectionTopicKind("tns1:RuleEngine/FaceDetector/ObjectDetection"));
    }

    [Fact]
    public void ExplicitTrueStateIsActive()
    {
        Assert.True(CameraEventClassifier.IsDetectionActive(Items(("State", "true"))));
    }

    [Theory]
    [InlineData("State")]
    [InlineData("IsMotion")]
    [InlineData("Motion")]
    public void ExplicitFalseStateIsInactiveWhicheverItemCarriesIt(string itemName)
    {
        // The falling edge is what closes a detection span — without this a person who left would
        // keep their span open until the session shut down.
        Assert.False(CameraEventClassifier.IsDetectionActive(Items((itemName, "false"))));
    }

    [Fact]
    public void ANotificationWithNoStateItemCountsAsActive()
    {
        // Deliberately the opposite of IsMotionActive's "never manufacture motion" stance: several
        // real firmwares only ever emit an object-detection notification when the object appears,
        // with no boolean at all. Treating those as inactive would disable the feature entirely on
        // those cameras.
        Assert.True(CameraEventClassifier.IsDetectionActive(Items(("ObjectId", "7"))));
        Assert.True(CameraEventClassifier.IsDetectionActive(Items()));
    }

    [Fact]
    public void AnUnparseableStateCountsAsActive()
    {
        Assert.True(CameraEventClassifier.IsDetectionActive(Items(("State", "yes"))));
    }

    [Fact]
    public void PlainMotionClassificationIsUnchangedByAnyOfThis()
    {
        // Regression guard on the existing behavior these additions sit beside.
        Assert.True(CameraEventClassifier.IsMotionActive(
            "tns1:RuleEngine/CellMotionDetector/Motion", Items(("State", "true"))));
        Assert.False(CameraEventClassifier.IsMotionActive(
            "tns1:RuleEngine/PeopleDetector/People", Items(("State", "true"))));
    }

    [Fact]
    public void EveryDetectionKindHasDistinctDisplayValues()
    {
        var kinds = Enum.GetValues<DetectionKind>();

        // Colors must be distinct from each other so classes are visually separable on the timeline,
        // and none may collide with the timeline's existing motion-green/recorded-blue.
        var colors = kinds.Select(DetectionDisplay.ColorHex).ToList();
        Assert.Equal(colors.Count, colors.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain("#28e070", colors, StringComparer.OrdinalIgnoreCase); // motion green
        Assert.DoesNotContain("#1e6fd9", colors, StringComparer.OrdinalIgnoreCase); // recorded blue

        Assert.All(kinds, kind =>
        {
            Assert.False(string.IsNullOrWhiteSpace(DetectionDisplay.Label(kind)));
            Assert.False(string.IsNullOrWhiteSpace(DetectionDisplay.Emoji(kind)));
        });
    }

    [Theory]
    [InlineData("tns1:RuleEngine/AnimalDetector/Animal", DetectionKind.Animal)]
    [InlineData("tns1:RuleEngine/PetDetector/Pet", DetectionKind.Animal)]
    [InlineData("tns1:RuleEngine/AbandonedObject/Object", DetectionKind.ObjectAppeared)]
    [InlineData("tns1:RuleEngine/ObjectAppearance/Object", DetectionKind.ObjectAppeared)]
    [InlineData("tns1:RuleEngine/MissingObject/Object", DetectionKind.ObjectMissing)]
    [InlineData("tns1:RuleEngine/ObjectRemoval/Object", DetectionKind.ObjectMissing)]
    public void VendorTopicsForTheNewClassesAreRecognized(string topic, DetectionKind expected)
        => Assert.Equal(expected, CameraEventClassifier.DetectionTopicKind(topic));

    [Fact]
    public void ASpecificObjectTopicWinsOverTheGenericObjectDetectorMarker()
    {
        // Both "AbandonedObject" and "ObjectDetection" could match a topic naming an abandoned
        // object; the specific class has to win or the distinction is lost to Other.
        Assert.Equal(DetectionKind.ObjectAppeared,
            CameraEventClassifier.DetectionTopicKind("tns1:RuleEngine/AbandonedObjectDetection/Object"));
    }

    [Fact]
    public void EveryKindHasItsOwnColorAndEmoji()
    {
        // A duplicate would make two classes indistinguishable on a timeline or a badge.
        var colors = DetectionDisplay.AllKinds.Select(DetectionDisplay.ColorHex).ToList();
        var emoji = DetectionDisplay.AllKinds.Select(DetectionDisplay.Emoji).ToList();

        Assert.Equal(colors.Count, colors.Distinct().Count());
        Assert.Equal(emoji.Count, emoji.Distinct().Count());
        // ...and none of them collides with plain motion or recorded coverage.
        Assert.DoesNotContain(EventColors.DefaultMotion, colors);
        Assert.DoesNotContain(EventColors.DefaultRecording, colors);
    }

    [Fact]
    public void AllKindsCoversEveryEnumValue()
    {
        // Guards the hand-ordered list against a class being added to the enum and silently never
        // appearing in the admin colour editor.
        Assert.Equal(
            Enum.GetValues<DetectionKind>().OrderBy(k => k),
            DetectionDisplay.AllKinds.OrderBy(k => k));
    }
}
