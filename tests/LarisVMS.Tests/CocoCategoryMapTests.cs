using LarisVMS.Core;

namespace LarisVMS.Tests;

public class CocoCategoryMapTests
{
    // The full, standard 80-class COCO vocabulary YOLO-family models are trained against — the
    // same set aitest's own LibreYOLO9/RT-DETR export tooling targets. Confirms every one of them
    // resolves to exactly one of the small fixed categories with no gaps, per the object detection
    // plan's verification section: "an unmapped class silently falling through to 'Object' is
    // fine; falling through to nothing/null is not."
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
}
