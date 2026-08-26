using LarisVMS.Core;

namespace LarisVMS.Tests;

public class CocoCategoryMapTests
{
    // The full, standard-spelling 80-class COCO vocabulary. Confirms every one of them resolves to
    // exactly one of the small fixed categories with no gaps, per the object detection plan's
    // verification section: "an unmapped class silently falling through to 'Object' is fine;
    // falling through to nothing/null is not." D-FINE's own obj2coco variant actually reports a few
    // of these under COCO's older PASCAL-VOC-era spellings instead (motorbike/aeroplane/sofa/
    // pottedplant/diningtable/tvmonitor) — covered separately below, since both spellings need to
    // resolve correctly regardless of which convention a given model uses.
    private static readonly string[] AllCocoClasses =
    [
        "person", "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat",
        "traffic light", "fire hydrant", "stop sign", "parking meter", "bench", "bird", "cat",
        "dog", "horse", "sheep", "cow", "elephant", "bear", "zebra", "giraffe", "backpack",
        "umbrella", "handbag", "tie", "suitcase", "frisbee", "skis", "snowboard", "sports ball",
        "kite", "baseball bat", "baseball glove", "skateboard", "surfboard", "tennis racket",
        "bottle", "wine glass", "cup", "fork", "knife", "spoon", "bowl", "banana", "apple",
        "sandwich", "orange", "broccoli", "carrot", "hot dog", "pizza", "donut", "cake", "chair",
        "couch", "potted plant", "bed", "dining table", "toilet", "tv", "laptop", "mouse",
        "remote", "keyboard", "cell phone", "microwave", "oven", "toaster", "sink", "refrigerator",
        "book", "clock", "vase", "scissors", "teddy bear", "hair drier", "toothbrush",
    ];

    [Fact]
    public void AllEightyCocoClassesResolveToAKnownCategory()
    {
        var known = new HashSet<string> { CocoCategoryMap.Human, CocoCategoryMap.Vehicle, CocoCategoryMap.Animal, CocoCategoryMap.Object };

        Assert.Equal(80, AllCocoClasses.Length);
        foreach (var cocoClass in AllCocoClasses)
        {
            var category = CocoCategoryMap.Resolve(cocoClass);
            Assert.False(string.IsNullOrWhiteSpace(category), $"'{cocoClass}' resolved to a blank category.");
            Assert.Contains(category, known);
        }
    }

    [Theory]
    [InlineData("person", CocoCategoryMap.Human)]
    [InlineData("car", CocoCategoryMap.Vehicle)]
    [InlineData("truck", CocoCategoryMap.Vehicle)]
    [InlineData("bicycle", CocoCategoryMap.Vehicle)]
    [InlineData("dog", CocoCategoryMap.Animal)]
    [InlineData("cat", CocoCategoryMap.Animal)]
    [InlineData("giraffe", CocoCategoryMap.Animal)]
    [InlineData("backpack", CocoCategoryMap.Object)]
    [InlineData("laptop", CocoCategoryMap.Object)]
    public void KnownClassesResolveToTheExpectedCategory(string cocoClass, string expectedCategory)
    {
        Assert.Equal(expectedCategory, CocoCategoryMap.Resolve(cocoClass));
    }

    [Fact]
    public void CaseInsensitiveMatch()
    {
        Assert.Equal(CocoCategoryMap.Human, CocoCategoryMap.Resolve("PERSON"));
        Assert.Equal(CocoCategoryMap.Vehicle, CocoCategoryMap.Resolve("Car"));
    }

    [Fact]
    public void UnrecognizedClassFallsThroughToObjectRatherThanThrowingOrReturningNull()
    {
        var category = CocoCategoryMap.Resolve("some-future-model-class-nobody-has-seen-yet");

        Assert.Equal(CocoCategoryMap.Object, category);
    }

    [Fact]
    public void NoTwoOfTheThreeNamedCategoriesOverlap()
    {
        // Every class in the vehicle/animal/human lists must resolve to exactly the category it's
        // listed under — a class accidentally present in two lists would silently pick whichever
        // one CocoCategoryMap.Resolve happens to check first.
        var vehicles = AllCocoClasses.Where(c => CocoCategoryMap.Resolve(c) == CocoCategoryMap.Vehicle).ToList();
        var animals = AllCocoClasses.Where(c => CocoCategoryMap.Resolve(c) == CocoCategoryMap.Animal).ToList();

        Assert.Empty(vehicles.Intersect(animals, StringComparer.OrdinalIgnoreCase));
    }

    // D-FINE's obj2coco variant reports these six classes under COCO's older PASCAL-VOC-era
    // spellings (see DFineLabels' own doc comment) rather than the modern ones AllCocoClasses above
    // uses — without explicit entries for the vehicle pair, a motorbike/aeroplane detection would
    // have silently fallen through to the "Object" catch-all instead of "Vehicle".
    [Theory]
    [InlineData("motorbike", CocoCategoryMap.Vehicle)]
    [InlineData("aeroplane", CocoCategoryMap.Vehicle)]
    [InlineData("sofa", CocoCategoryMap.Object)]
    [InlineData("pottedplant", CocoCategoryMap.Object)]
    [InlineData("diningtable", CocoCategoryMap.Object)]
    [InlineData("tvmonitor", CocoCategoryMap.Object)]
    public void DFineLegacySpellingsResolveCorrectly(string legacySpelling, string expectedCategory)
    {
        Assert.Equal(expectedCategory, CocoCategoryMap.Resolve(legacySpelling));
    }

    [Fact]
    public void EveryObjects365ClassResolvesToAKnownCategory()
    {
        var known = new HashSet<string> { CocoCategoryMap.Human, CocoCategoryMap.Vehicle, CocoCategoryMap.Animal, CocoCategoryMap.Object };

        // Index 0 ("None") is Objects365's padding slot — DFineDecoder filters it out before it
        // ever reaches CocoCategoryMap.Resolve, so it's deliberately excluded here too.
        foreach (var label in LarisVMS.Vision.Inference.DFineLabels.Obj365.Skip(1))
        {
            var category = CocoCategoryMap.Resolve(label);
            Assert.False(string.IsNullOrWhiteSpace(category), $"'{label}' resolved to a blank category.");
            Assert.Contains(category, known);
        }
    }

    [Theory]
    [InlineData("Person", CocoCategoryMap.Human)]
    [InlineData("SUV", CocoCategoryMap.Vehicle)]
    [InlineData("Pickup Truck", CocoCategoryMap.Vehicle)]
    [InlineData("Wild Bird", CocoCategoryMap.Animal)]
    [InlineData("Rickshaw", CocoCategoryMap.Vehicle)]
    [InlineData("Lion", CocoCategoryMap.Animal)]
    [InlineData("Chair", CocoCategoryMap.Object)]
    public void KnownObjects365ClassesResolveToTheExpectedCategory(string label, string expectedCategory)
    {
        Assert.Equal(expectedCategory, CocoCategoryMap.Resolve(label));
    }
}
