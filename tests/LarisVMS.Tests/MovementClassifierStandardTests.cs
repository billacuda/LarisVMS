using LarisVMS.Vision.Detection;
using SkiaSharp;

namespace LarisVMS.Tests;

/// <summary>
/// MovementMode.Standard against synthetic tracks: 7 fps (the default Detection.MaxFps), a
/// 1920x1080 frame, and seeded Gaussian wobble on every box edge (σ as a fraction of the box's
/// width/height) — so each case states both what it must classify and how quickly.
///
/// Every case also runs with an uneven cadence, as real detection does: the frame slot keeps only
/// the newest frame, so whenever inference runs long a frame is skipped, and gaps vary from about a
/// tenth of a second to more than double the nominal one.
/// </summary>
public class MovementClassifierStandardTests
{
    private const double Fps = 7;
    private const int FrameW = 1920, FrameH = 1080;
    private static readonly DateTime BaseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed record RunResult(double? FirstMovingSec, int? FirstMovingIndex, int MovingFrames, MovementState Final,
        IReadOnlyList<MovementState> States, IReadOnlyList<double> Times, string Reasons);

    private static MovementClassifier Standard(MovementSensitivity sensitivity = MovementSensitivity.Normal) =>
        new(new MovementClassifierOptions { Mode = MovementMode.Standard, Sensitivity = sensitivity });

    /// <summary>Observes track 1 for <paramref name="frames"/> nominal frames' worth of time. Its true
    /// box is <paramref name="box"/>(f) (left, top, right, bottom), where f is the time in nominal
    /// frames (seconds × 7), so the same function describes the object whatever the cadence.
    /// <paramref name="uneven"/> spaces the samples 0.1–0.3 s apart, sometimes twice that.
    /// <paramref name="others"/> adds another tracked box to each frame.</summary>
    private static RunResult Run(MovementClassifier classifier, int frames, Func<double, (double L, double T, double R, double B)> box,
        double sigmaFraction, int seed = 1, bool uneven = false, Func<double, (double L, double T, double R, double B)?>? others = null,
        Func<double, (double L, double T, double R, double B)>? clip = null)
    {
        var random = new Random(seed);
        var cadence = new Random(seed + 1000);
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());

        double? first = null;
        int? firstIndex = null;
        var moving = 0;
        var states = new List<MovementState>();
        var times = new List<double>();
        var reasons = new List<string>();
        var duration = (frames - 0.5) / Fps;
        for (double sec = 0; sec <= duration;)
        {
            var f = sec * Fps;
            var (l, t, r, b) = box(f);
            var w = r - l;
            var h = b - t;
            l += Gauss() * sigmaFraction * w;
            r += Gauss() * sigmaFraction * w;
            t += Gauss() * sigmaFraction * h;
            b += Gauss() * sigmaFraction * h;
            if (clip is not null) (l, t, r, b) = clip(f) is var c ? (Math.Max(l, c.L), Math.Max(t, c.T), Math.Min(r, c.R), Math.Min(b, c.B)) : (l, t, r, b);
            l = Math.Clamp(l, 0, FrameW); r = Math.Clamp(r, 0, FrameW);
            t = Math.Clamp(t, 0, FrameH); b = Math.Clamp(b, 0, FrameH);
            var rect = new SKRectI((int)Math.Round(l), (int)Math.Round(t), (int)Math.Round(r), (int)Math.Round(b));

            var frameBoxes = new List<(int, SKRectI)> { (1, rect) };
            if (others?.Invoke(f) is { } o)
                frameBoxes.Add((2, new SKRectI((int)o.L, (int)o.T, (int)o.R, (int)o.B)));
            classifier.BeginFrame(frameBoxes);

            var observation = classifier.Observe(1, rect, 0.8, FrameW, FrameH, BaseTime.AddSeconds(sec));
            var state = observation.State;
            if (observation.Reason is { } why) reasons.Add($"{why}@{sec:0.00}s");
            if (state == MovementState.Moving)
            {
                moving++;
                first ??= sec;
                firstIndex ??= states.Count;
            }
            states.Add(state);
            times.Add(sec);

            if (!uneven) sec = Math.Round((sec * Fps) + 1) / Fps;
            else
            {
                var gap = 0.1 + 0.2 * cadence.NextDouble();
                if (cadence.NextDouble() < 0.15) gap *= 2; // a skipped frame
                sec += gap;
            }
        }
        return new RunResult(first, firstIndex, moving, states[^1], states, times, string.Join(", ", reasons));
    }

    private static (double, double, double, double) Box(double cx, double cy, double w, double h) =>
        (cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2);

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void AParkedCarWithThreePercentWobbleStaysIdleForAMinute(int seed, bool uneven)
    {
        // 320x240: a 400px diagonal.
        var result = Run(Standard(), (int)(60 * Fps), _ => Box(900, 600, 320, 240), 0.03, seed, uneven);
        Assert.True(result.MovingFrames == 0, result.Reasons);
    }

    // Several box sizes and many seeds, so "never jitters into Moving" isn't luck of one seed.
    [Theory]
    [InlineData(40, 30, 0.03, false)]
    [InlineData(120, 90, 0.03, false)]
    [InlineData(720, 540, 0.03, false)]
    [InlineData(320, 240, 0.05, false)]
    [InlineData(40, 30, 0.03, true)]
    [InlineData(120, 90, 0.03, true)]
    [InlineData(720, 540, 0.03, true)]
    [InlineData(320, 240, 0.05, true)]
    public void WobbleNeverReadsAsMovementAcrossSizesAndSeeds(double w, double h, double sigma, bool uneven)
    {
        for (var seed = 1; seed <= 20; seed++)
        {
            var result = Run(Standard(), (int)(60 * Fps), _ => Box(900, 500, w, h), sigma, seed, uneven);
            Assert.True(result.MovingFrames == 0, $"seed {seed}: {result.Reasons}");
        }
    }

    // A potted plant: the detector's box flips between two shapes (leaves included or not) and holds
    // each for a while. One side jumps, the other stays put, and the height changes — a step, not a
    // path, and it must never count as movement.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ABoxThatJumpsBetweenTwoShapesIsNotMovement(bool topEdge, bool uneven)
    {
        for (var seed = 1; seed <= 10; seed++)
        {
            var flips = new Random(seed + 500);
            var tall = false;
            var nextFlip = 0.0;
            var result = Run(Standard(), (int)(60 * Fps), f =>
            {
                var sec = f / Fps;
                if (sec >= nextFlip)
                {
                    tall = !tall;
                    nextFlip = sec + 0.5 + 4 * flips.NextDouble();
                }
                var grow = tall ? 45 : 0;
                // Grows upward (leaves) or sideways (a pot next to it).
                return topEdge ? (700, 400 - grow, 820, 560) : (700, 400, 820 + grow, 560);
            }, 0.015, seed, uneven);
            Assert.True(result.MovingFrames == 0, $"seed {seed}: {result.Reasons}");
        }
    }

    // A bookshelf against the house at the picture's left edge, seen as a "car": its box is cut off by
    // the left and bottom borders, and its right edge jumps between two widths, for minutes on end.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnObjectCutOffAtTheBorderThatChangesWidthIsNotMovement(bool uneven)
    {
        for (var seed = 1; seed <= 10; seed++)
        {
            var flips = new Random(seed + 900);
            var wide = false;
            var nextFlip = 0.0;
            var result = Run(Standard(), (int)(180 * Fps), f =>
            {
                var sec = f / Fps;
                if (sec >= nextFlip)
                {
                    wide = !wide;
                    nextFlip = sec + 0.3 + 3 * flips.NextDouble();
                }
                return (-20, FrameH - 480, wide ? 750 : 600, FrameH + 20);
            }, 0.01, seed, uneven);
            Assert.True(result.MovingFrames == 0, $"seed {seed}: {result.Reasons}");
        }
    }

    // A distant parked car whose box slowly creeps (lighting, compression) by a few pixels and back.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AParkedCarsBoxCreepingAFewPixelsIsNotMovement(bool uneven)
    {
        for (var seed = 1; seed <= 10; seed++)
        {
            var result = Run(Standard(), (int)(120 * Fps), f =>
            {
                var sec = f / Fps;
                var creep = 12 * Math.Sin(sec / 6.0) + 4 * Math.Sin(sec * 1.7);
                return Box(2900 / 2.0 + creep, 250, 240, 150);
            }, 0.01, seed, uneven);
            Assert.True(result.MovingFrames == 0, $"seed {seed}: {result.Reasons}");
        }
    }

    [Theory]
    [InlineData(MovementSensitivity.Low)]
    [InlineData(MovementSensitivity.Normal)]
    public void HeavierWobbleStaysIdleAtLowAndNormal(MovementSensitivity sensitivity)
    {
        for (var seed = 1; seed <= 3; seed++)
        {
            var result = Run(Standard(sensitivity), (int)(60 * Fps), _ => Box(900, 600, 320, 240), 0.05, seed);
            Assert.True(result.MovingFrames == 0, $"seed {seed}: {result.Reasons}");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATruckPullingOutIsMovingWithinAFewFrames(bool uneven)
    {
        // 720x540 (900px diagonal), parked for 5 s, then backing out at a crawl: about 1 m/s, 120 px/s
        // at this size. Under the old rule it would need 450px inside 2 s.
        const double parkedSec = 5;
        var result = Run(Standard(), (int)(8 * Fps), f =>
            Box(1000 - (f / Fps < parkedSec ? 0 : 120 * (f / Fps - parkedSec)), 600, 720, 540), 0.01, uneven: uneven);
        AssertMovingWithinSamples(result, parkedSec, 5);
        Assert.Equal(MovementState.Moving, result.Final);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EvenASlowCreepIsPickedUpWithinThreeSeconds(bool uneven)
    {
        // The same truck at 60 px/s (about half a metre a second): each frame's step is close to the
        // box's own wobble, so it takes a steady line over a second or two to tell it apart.
        var result = Run(Standard(), (int)(6 * Fps), f => Box(1000 - 60 * f / Fps, 600, 720, 540), 0.01, uneven: uneven);
        Assert.NotNull(result.FirstMovingSec);
        Assert.True(result.FirstMovingSec <= 3.0, $"took {result.FirstMovingSec:0.00}s");
    }

    /// <summary>Moving no later than the <paramref name="samples"/>th sample taken after
    /// <paramref name="startSec"/>, when the object started moving.</summary>
    private static void AssertMovingWithinSamples(RunResult result, double startSec, int samples)
    {
        var firstAfterStart = result.Times.ToList().FindIndex(t => t >= startSec);
        Assert.True(result.FirstMovingIndex is { } i && i >= firstAfterStart && i - firstAfterStart < samples,
            $"first moving at sample {result.FirstMovingIndex} ({result.FirstMovingSec:0.00}s); movement started at sample {firstAfterStart}; {result.Reasons}");
    }

    [Fact]
    public void TheSameTruckIsNeverMovingUnderTheLegacyRule()
    {
        var legacy = new MovementClassifier();
        var result = Run(legacy, (int)(6 * Fps), f => Box(1000 - 30 * f / Fps, 600, 720, 540), 0.01);
        Assert.Equal(0, result.MovingFrames);
        var crawl = Run(new MovementClassifier(), (int)(3 * Fps), f => Box(1000 - 120 * f / Fps, 600, 720, 540), 0.01);
        Assert.True(crawl.FirstMovingSec is null or > 1.5, $"legacy took {crawl.FirstMovingSec:0.00}s");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AWalkingPersonIsMovingWithinAFewFrames(bool uneven)
    {
        // 50x110 (about 120px diagonal) walking at about 1.4 m/s: 90 px/s at this size.
        var result = Run(Standard(), (int)(3 * Fps), f => Box(400 + 90 * f / Fps, 500, 50, 110), 0.02, uneven: uneven);
        AssertMovingWithinSamples(result, 0, 5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASmallFastCarIsMovingOnItsThirdSample(bool uneven)
    {
        // 32x24 (40px diagonal) at 300 px/s.
        var result = Run(Standard(), 10, f => Box(200 + 300 * f / Fps, 300, 32, 24), 0.02, uneven: uneven);
        Assert.True(result.FirstMovingIndex <= (uneven ? 3 : 2), $"sample {result.FirstMovingIndex}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACarCrossingTheWholeViewInUnderASecondIsMovingOnItsThirdSample(bool uneven)
    {
        // 1920px in 0.8 s, so it's only on screen for about 6 frames.
        var result = Run(Standard(), 6, f => Box(150 + 2400 * f / Fps, 700, 200, 120), 0.02, uneven: uneven);
        Assert.True(result.FirstMovingIndex <= (uneven ? 3 : 2), $"sample {result.FirstMovingIndex}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACarEnteringFromTheLeftEdgeIsMovingOnItsThirdOrFourthSample(bool uneven)
    {
        // 300px long, its left end hidden past the frame edge until it's fully in.
        var result = Run(Standard(), 14, f => (-260 + 200 * f / Fps, 500, 40 + 200 * f / Fps, 680), 0.01, uneven: uneven);
        Assert.True(result.FirstMovingIndex <= (uneven ? 4 : 3), $"sample {result.FirstMovingIndex}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACarComingOutFromBehindAPoleIsMovingWithinASecond(bool uneven)
    {
        // The pole hides everything left of x=800: the box's left edge stays put while its right
        // edge advances at 150 px/s.
        var result = Run(Standard(), (int)(2 * Fps), f => Box(700 + 150 * f / Fps, 500, 300, 180), 0.01, uneven: uneven,
            clip: _ => (800, 0, FrameW, FrameH));
        AssertMovingWithinSamples(result, 0, 5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AParkedCarStaysIdleWhileAPersonWalksAcrossIt(bool uneven)
    {
        // The person's box sweeps over the car; while it covers the car's edges, the car's own box is
        // cut back to the person's edge, as a detector does.
        static (double L, double T, double R, double B) Person(double f) => (450 + 80 * f / Fps, 420, 510 + 80 * f / Fps, 590);
        var result = Run(Standard(), (int)(12 * Fps), f =>
        {
            var p = Person(f);
            double l = 600, r = 1000;
            if (p.L < l && p.R > l) l = p.R;           // covering the left edge
            if (p.L < r && p.R > r) r = p.L;           // covering the right edge
            return (l, 450, r, 700);
        }, 0.01, uneven: uneven, others: f => Person(f));
        Assert.True(result.MovingFrames == 0, result.Reasons);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACarDrivingStraightAtTheCameraIsMoving(bool uneven)
    {
        // Centre stays put; the box grows from 100 to 250px tall over 2 s.
        var result = Run(Standard(), (int)(2 * Fps), f =>
        {
            var h = 100 + 75 * f / Fps;
            return Box(960, 500 + h / 4, h * 1.3, h);
        }, 0.01, uneven: uneven);
        Assert.NotNull(result.FirstMovingSec);
        Assert.Equal(MovementState.Moving, result.Final);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACarSlowingToAStopStaysMovingThroughTheSlowdownThenGoesIdle(bool uneven)
    {
        // 150 px/s for 2 s, slowing to a stop over 1 s, then parked for 6 s.
        static double X(double t) => t < 2 ? 150 * t : t < 3 ? 300 + 150 * (t - 2) - 75 * (t - 2) * (t - 2) : 375;
        var result = Run(Standard(), (int)(9 * Fps), f => Box(300 + X(f / Fps), 500, 240, 150), 0.01, uneven: uneven);

        Assert.NotNull(result.FirstMovingIndex);
        for (var i = result.FirstMovingIndex!.Value; i < result.Times.Count && result.Times[i] <= 3; i++)
            Assert.True(result.States[i] == MovementState.Moving, $"Idle at {result.Times[i]:0.00}s during the drive");
        Assert.Equal(MovementState.Idle, result.Final);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACarParkedForAWhileThatDrivesOffIsMovingWithinAFewFrames(bool uneven)
    {
        // Parked (with wobble) for 10 s, then away at 200 px/s.
        const double parkedSec = 10;
        var result = Run(Standard(), (int)(12 * Fps), f =>
            Box(800 + (f / Fps < parkedSec ? 0 : 200 * (f / Fps - parkedSec)), 600, 320, 200), 0.02, uneven: uneven);
        AssertMovingWithinSamples(result, parkedSec, 5);
    }

    [Fact]
    public void ATrackThatTravelledButNeverCountedIsSummarizedWhenItEnds()
    {
        // A one-jump "travel" (the tracker swapping two objects) never counts — so it's the kind of
        // thing the summary has to explain.
        var classifier = Standard();
        for (var i = 0; i < 10; i++)
        {
            var x = i < 5 ? 300 : 700;
            classifier.Observe(7, new SKRectI(x, 300, x + 200, 420), 0.8, FrameW, FrameH, BaseTime.AddSeconds(i / Fps), "car");
        }
        classifier.Prune(new HashSet<int>());
        var missed = Assert.Single(classifier.TakeMissedTracks());
        Assert.Contains("track 7 (car)", missed);
        Assert.Empty(classifier.TakeMissedTracks());
    }

    [Fact]
    public void AParkedTrackThatEndsIsNotSummarized()
    {
        var classifier = Standard();
        for (var i = 0; i < 20; i++)
            classifier.Observe(1, new SKRectI(300, 300, 500, 420), 0.8, FrameW, FrameH, BaseTime.AddSeconds(i / Fps));
        classifier.Prune(new HashSet<int>());
        Assert.Empty(classifier.TakeMissedTracks());
    }

    [Fact]
    public void OnlyTheChosenKindsOfObjectCountAsMoving()
    {
        var defaults = LarisVMS.Core.CocoCategoryMap.ParseMovingObjects(null);
        Assert.True(LarisVMS.Core.CocoCategoryMap.CountsAsMoving(defaults, "Vehicle", "truck"));
        Assert.True(LarisVMS.Core.CocoCategoryMap.CountsAsMoving(defaults, "Human", "person"));
        Assert.False(LarisVMS.Core.CocoCategoryMap.CountsAsMoving(defaults, "Object", "potted plant"));

        var withPackages = LarisVMS.Core.CocoCategoryMap.ParseMovingObjects("Human, suitcase,Package");
        Assert.True(LarisVMS.Core.CocoCategoryMap.CountsAsMoving(withPackages, "Object", "Suitcase"));
        Assert.True(LarisVMS.Core.CocoCategoryMap.CountsAsMoving(withPackages, "Object", "package"));
        Assert.False(LarisVMS.Core.CocoCategoryMap.CountsAsMoving(withPackages, "Vehicle", "car"));

        Assert.Empty(LarisVMS.Core.CocoCategoryMap.ParseMovingObjects(""));
    }

    [Fact]
    public void TheFirstMovingObservationSaysWhichRuleFired()
    {
        var classifier = Standard();
        string? reason = null;
        for (var i = 0; i < 6 && reason is null; i++)
        {
            var x = 200 + (int)(300 * i / Fps);
            reason = classifier.Observe(1, new SKRectI(x, 300, x + 32, 324), 0.8, FrameW, FrameH, BaseTime.AddSeconds(i / Fps)).Reason;
        }
        Assert.StartsWith("steps", reason);
    }
}
