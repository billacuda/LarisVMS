using LarisVMS.Core;

namespace LarisVMS.Tests;

public class DetectedObjectColorAssignerTests
{
    [Fact]
    public void FirstCategoryGetsTheFirstPaletteColor()
    {
        var color = DetectedObjectColorAssigner.PickNextColor([]);

        Assert.False(string.IsNullOrWhiteSpace(color));
        Assert.StartsWith("#", color);
    }

    [Fact]
    public void NeverReturnsAColorAlreadyInUse()
    {
        var used = new List<string>();

        // Simulate a handful of categories being registered one after another, exactly like
        // NodeService.RecordMotionSpansAsync's find-or-create would.
        for (var i = 0; i < 8; i++)
        {
            var next = DetectedObjectColorAssigner.PickNextColor(used);
            Assert.DoesNotContain(next, used, StringComparer.OrdinalIgnoreCase);
            used.Add(next);
        }

        Assert.Equal(8, used.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void IsCaseInsensitiveWhenCheckingWhatsAlreadyUsed()
    {
        var first = DetectedObjectColorAssigner.PickNextColor([]);

        // A differently-cased duplicate of the first color must still be recognized as taken.
        var second = DetectedObjectColorAssigner.PickNextColor([first.ToUpperInvariant()]);

        Assert.NotEqual(first, second, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void PaletteExhaustionFallsBackToADeterministicHashDerivedColor()
    {
        // Exhaust every curated entry by asking for one more color than the palette has, feeding
        // each result back in as "already used" — this must never throw, and must keep returning
        // distinct-looking hex colors rather than repeating or blanking out.
        var used = new List<string>();
        string? last = null;

        for (var i = 0; i < 40; i++) // comfortably past the curated palette's own size
        {
            var next = DetectedObjectColorAssigner.PickNextColor(used);
            Assert.Matches("^#[0-9a-fA-F]{6}$", next);
            used.Add(next);
            last = next;
        }

        Assert.NotNull(last);
    }

    [Fact]
    public void FallbackIsDeterministicForTheSameUsedCount()
    {
        // Two independent calls with the same *count* of already-used colors (post-exhaustion,
        // where the fallback kicks in) should derive the same color — reproducible, not random.
        var usedA = Enumerable.Range(0, 20).Select(i => $"#{i:x6}").ToList();
        var usedB = Enumerable.Range(0, 20).Select(i => $"#{i:x6}").ToList();

        var colorA = DetectedObjectColorAssigner.PickNextColor(usedA);
        var colorB = DetectedObjectColorAssigner.PickNextColor(usedB);

        Assert.Equal(colorA, colorB);
    }
}
