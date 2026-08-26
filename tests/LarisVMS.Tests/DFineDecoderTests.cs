using LarisVMS.Vision.Inference;

namespace LarisVMS.Tests;

/// <summary>
/// Covers DFineDecoder against small, hand-computed synthetic tensors — the sigmoid/threshold/
/// cxcywh→xyxy math is verified independently against a real model run (see the class's own doc
/// comment), so these pin the exact arithmetic and edge-case handling rather than re-verifying
/// model behavior.
/// </summary>
public class DFineDecoderTests
{
    private static readonly string[] Labels = ["cat", "dog", "None"];

    // sigmoid(4) ≈ 0.982, sigmoid(-4) ≈ 0.018 — comfortably above/below any threshold used below.
    private const float HighLogit = 4f;
    private const float LowLogit = -4f;

    [Fact]
    public void DecodesAConfidentDetectionWithCorrectBoxConversion()
    {
        // One query, class 0 ("cat") confident, others not. Box: cx=0.5,cy=0.5,w=0.4,h=0.2 against
        // a 100x200 frame -> x:[30,70], y:[80,120].
        float[] logits = [HighLogit, LowLogit, LowLogit];
        float[] boxes = [0.5f, 0.5f, 0.4f, 0.2f];

        var results = DFineDecoder.Decode(logits, boxes, numQueries: 1, numClasses: 3, Labels,
            confidenceThreshold: 0.5, frameWidth: 100, frameHeight: 200);

        var d = Assert.Single(results);
        Assert.Equal("cat", d.Label.Name);
        Assert.Equal(0, d.Label.Index);
        Assert.Equal(30, d.BoundingBox.Left);
        Assert.Equal(80, d.BoundingBox.Top);
        Assert.Equal(70, d.BoundingBox.Right);
        Assert.Equal(120, d.BoundingBox.Bottom);
        Assert.True(d.Confidence > 0.9);
    }

    [Fact]
    public void FiltersOutCandidatesBelowTheConfidenceThreshold()
    {
        float[] logits = [LowLogit, LowLogit, LowLogit];
        float[] boxes = [0.5f, 0.5f, 0.2f, 0.2f];

        var results = DFineDecoder.Decode(logits, boxes, numQueries: 1, numClasses: 3, Labels,
            confidenceThreshold: 0.5, frameWidth: 100, frameHeight: 100);

        Assert.Empty(results);
    }

    [Fact]
    public void NeverReportsTheNoneLabelEvenWhenItScoresHighest()
    {
        // Class index 2 ("None") is Objects365's padding slot — confident or not, it must never
        // become a reported detection.
        float[] logits = [LowLogit, LowLogit, HighLogit];
        float[] boxes = [0.5f, 0.5f, 0.2f, 0.2f];

        var results = DFineDecoder.Decode(logits, boxes, numQueries: 1, numClasses: 3, Labels,
            confidenceThreshold: 0.5, frameWidth: 100, frameHeight: 100);

        Assert.Empty(results);
    }

    [Fact]
    public void SkipsADegenerateBoxThatClampsToZeroSize()
    {
        // A box entirely outside the frame (cx=2.0) clamps to a zero-width rectangle at the edge.
        float[] logits = [HighLogit, LowLogit, LowLogit];
        float[] boxes = [2.0f, 0.5f, 0.1f, 0.1f];

        var results = DFineDecoder.Decode(logits, boxes, numQueries: 1, numClasses: 3, Labels,
            confidenceThreshold: 0.5, frameWidth: 100, frameHeight: 100);

        Assert.Empty(results);
    }

    [Fact]
    public void OrdersMultipleDetectionsByDescendingConfidence()
    {
        // Query 0 very confident, query 1 moderately confident, both class "dog".
        float[] logits = [LowLogit, 6f, LowLogit, LowLogit, 1f, LowLogit];
        float[] boxes = [0.3f, 0.3f, 0.1f, 0.1f, 0.7f, 0.7f, 0.1f, 0.1f];

        var results = DFineDecoder.Decode(logits, boxes, numQueries: 2, numClasses: 3, Labels,
            confidenceThreshold: 0.5, frameWidth: 100, frameHeight: 100);

        Assert.Equal(2, results.Count);
        Assert.True(results[0].Confidence >= results[1].Confidence);
    }

    [Fact]
    public void ThrowsWhenLogitsLengthDoesNotMatchQueriesTimesClasses()
    {
        float[] logits = [HighLogit, LowLogit]; // wrong length for numClasses=3
        float[] boxes = [0.5f, 0.5f, 0.2f, 0.2f];

        Assert.Throws<ArgumentException>(() =>
            DFineDecoder.Decode(logits, boxes, numQueries: 1, numClasses: 3, Labels, 0.5, 100, 100));
    }

    [Fact]
    public void ThrowsWhenLabelsCountDoesNotMatchNumClasses()
    {
        float[] logits = [HighLogit, LowLogit, LowLogit];
        float[] boxes = [0.5f, 0.5f, 0.2f, 0.2f];

        Assert.Throws<ArgumentException>(() =>
            DFineDecoder.Decode(logits, boxes, numQueries: 1, numClasses: 3, ["only-one-label"], 0.5, 100, 100));
    }

    [Fact]
    public void RespectsMaxDetectionsCap()
    {
        // Three queries, all confidently "cat", capped to 2 results.
        float[] logits = [HighLogit, LowLogit, LowLogit, HighLogit, LowLogit, LowLogit, HighLogit, LowLogit, LowLogit];
        float[] boxes = [0.2f, 0.2f, 0.1f, 0.1f, 0.5f, 0.5f, 0.1f, 0.1f, 0.8f, 0.8f, 0.1f, 0.1f];

        var results = DFineDecoder.Decode(logits, boxes, numQueries: 3, numClasses: 3, Labels,
            confidenceThreshold: 0.5, frameWidth: 100, frameHeight: 100, maxDetections: 2);

        Assert.Equal(2, results.Count);
    }
}
