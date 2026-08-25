using SkiaSharp;
using YoloDotNet.Models;
using YoloDotNet.Models.Interfaces;

namespace LarisVMS.Tests;

/// <summary>
/// Minimal <see cref="IDetection"/> for testing the tracker without depending on YoloDotNet's own
/// result types, which are sealed to being produced by inference. Ported from aitest's own
/// Aitest.Vision.Tests.Tracking.FakeDetection (MIT), unchanged apart from its namespace.
/// </summary>
internal sealed class FakeDetection : IDetection
{
    public required LabelModel Label { get; init; }
    public required double Confidence { get; init; }
    public required SKRectI BoundingBox { get; init; }
    public int? Id { get; set; }
    public List<SKPoint>? Tail { get; set; }

    public static FakeDetection Box(int x, int y, int w, int h, double confidence = 0.9, int classId = 0)
        => new()
        {
            Label = new LabelModel { Index = classId, Name = $"class{classId}" },
            Confidence = confidence,
            BoundingBox = new SKRectI(x, y, x + w, y + h),
        };
}
