using System.Text.Json;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Enums;
using LarisVMS.Vision.Inference;

namespace LarisVMS.Tests;

/// <summary>
/// Covers <see cref="ExternalDetectionMapper"/> — the translation of an external HTTP inference
/// service's JSON response into this codebase's source-pixel <c>ObjectDetection</c> shape. The two
/// geometry paths (letterbox/stretch inverse for a plain request, uniform capture→source scale for
/// a sliced one) are pinned here the same way <c>DFineDecoderTests</c> pins D-FINE's decode math.
/// </summary>
public class ExternalDetectionMapperTests
{
    private static ExternalDetectionResult Box(double x1, double y1, double x2, double y2, int cls = 0,
        string name = "person", double conf = 0.9) =>
        new(new ExternalDetectionBox(x1, y1, x2, y2), cls, name, conf);

    [Fact]
    public void PlainResponse_WithIdentityStretchProfile_PassesPixelsThrough()
    {
        // Stretch with source == network is an identity on the box: 640x640 in, 640x640 source out.
        var profile = InferenceProfile.Create(640, 640, AspectMode.Stretch, 640);

        var mapped = ExternalDetectionMapper.MapPlainResponse([Box(100, 150, 200, 300)], profile);

        var d = Assert.Single(mapped);
        Assert.Equal(100, d.BoundingBox.Left);
        Assert.Equal(150, d.BoundingBox.Top);
        Assert.Equal(200, d.BoundingBox.Right);
        Assert.Equal(300, d.BoundingBox.Bottom);
        Assert.Equal("person", d.Label.Name);
        Assert.Equal(0, d.Label.Index);
        Assert.Equal(0.9, d.Confidence);
    }

    [Fact]
    public void PlainResponse_WithStretchProfile_ScalesToSourceAspect()
    {
        // Stretch keeps the normalized coordinate identical, so a 640-space box scales straight onto
        // the source frame's own dimensions per axis.
        var profile = InferenceProfile.Create(1280, 720, AspectMode.Stretch, 640);

        var mapped = ExternalDetectionMapper.MapPlainResponse([Box(64, 64, 320, 320)], profile);

        var d = Assert.Single(mapped);
        // x: 64/640..320/640 -> *1280 = 128..640 ; y: 64/640..320/640 -> *720 = 72..360
        Assert.Equal(128, d.BoundingBox.Left);
        Assert.Equal(72, d.BoundingBox.Top);
        Assert.Equal(640, d.BoundingBox.Right);
        Assert.Equal(360, d.BoundingBox.Bottom);
    }

    [Fact]
    public void PlainResponse_WithLetterboxProfile_UndoesThePad()
    {
        // 1280x640 source, letterboxed into 640x640: scaled to 640x320, 160px pad top and bottom.
        var profile = InferenceProfile.Create(1280, 640, AspectMode.Letterbox, 640);
        Assert.Equal(160, profile.PadTop);

        // A box filling the content region (x 0..640, y 160..480 in network space) is the whole frame.
        var mapped = ExternalDetectionMapper.MapPlainResponse([Box(0, 160, 640, 480)], profile);

        var d = Assert.Single(mapped);
        Assert.Equal(0, d.BoundingBox.Left);
        Assert.Equal(0, d.BoundingBox.Top);
        Assert.Equal(1280, d.BoundingBox.Right);
        Assert.Equal(640, d.BoundingBox.Bottom);
    }

    [Fact]
    public void PlainResponse_DropsDegenerateBoxes()
    {
        var profile = InferenceProfile.Create(640, 640, AspectMode.Stretch, 640);
        var mapped = ExternalDetectionMapper.MapPlainResponse([Box(300, 300, 300, 320)], profile);
        Assert.Empty(mapped);
    }

    [Fact]
    public void SlicedResponse_MapsCapturePixelsUniformlyOntoSource()
    {
        // 1920x1080 landscape -> short edge 1080 scaled to 640, long edge 1920*640/1080 ~= 1138.
        var layout = SliceLayout.Create(1920, 1080, 640);
        Assert.Equal(640, layout.CaptureHeight);

        // A full-height box across the left third of the capture buffer.
        var mapped = ExternalDetectionMapper.MapSlicedResponse(
            [Box(0, 0, layout.CaptureWidth / 3.0, 640)], layout, 1920, 1080);

        var d = Assert.Single(mapped);
        Assert.Equal(0, d.BoundingBox.Left);
        Assert.Equal(0, d.BoundingBox.Top);
        Assert.Equal(640, d.BoundingBox.Right);   // (CaptureWidth/3) * (1920/CaptureWidth) == 640
        Assert.Equal(1080, d.BoundingBox.Bottom);
    }

    [Fact]
    public void SlicedResponse_ClampsToSourceBounds()
    {
        var layout = SliceLayout.Create(1920, 1080, 640);
        var mapped = ExternalDetectionMapper.MapSlicedResponse(
            [Box(-20, -10, layout.CaptureWidth + 50, layout.CaptureHeight + 50)], layout, 1920, 1080);

        var d = Assert.Single(mapped);
        Assert.Equal(0, d.BoundingBox.Left);
        Assert.Equal(0, d.BoundingBox.Top);
        Assert.Equal(1920, d.BoundingBox.Right);
        Assert.Equal(1080, d.BoundingBox.Bottom);
    }

    [Fact]
    public void BuildSliceSpec_MirrorsTheLayoutTiles()
    {
        var layout = SliceLayout.Create(1920, 1080, 640);
        var spec = ExternalDetectionMapper.BuildSliceSpec(layout);

        Assert.Equal(layout.CaptureWidth, spec.FullWidth);
        Assert.Equal(layout.CaptureHeight, spec.FullHeight);
        Assert.Equal(layout.Slices.Count, spec.Tiles.Count);
        for (var i = 0; i < layout.Slices.Count; i++)
        {
            Assert.Equal(layout.Slices[i].X, spec.Tiles[i].X);
            Assert.Equal(layout.Slices[i].Y, spec.Tiles[i].Y);
        }
    }

    /// <summary>Pins the exact <c>slice=</c> query-string shape <c>HttpDetectionEngine</c> sends and
    /// SideGlance's own <c>TryParseSliceQuery</c> parses: comma-separated <c>XxY</c> tile origins,
    /// full_width/full_height omitted (the service defaults those to the submitted image's own
    /// decoded dimensions — see <see cref="ExternalDetectionMapper.FormatSliceQuery"/>'s own doc
    /// comment for why repeating them would add nothing).</summary>
    [Fact]
    public void FormatSliceQuery_JoinsTileOriginsAsXxYPairs()
    {
        var spec = new ExternalSliceSpec(1280, 640,
        [
            new ExternalSliceTile(0, 0),
            new ExternalSliceTile(320, 0),
            new ExternalSliceTile(640, 0),
        ]);

        Assert.Equal("0x0,320x0,640x0", ExternalDetectionMapper.FormatSliceQuery(spec));
    }

    [Fact]
    public void FormatSliceQuery_RoundTripsThroughBuildSliceSpec()
    {
        var layout = SliceLayout.Create(1920, 1080, 640);

        var query = ExternalDetectionMapper.FormatSliceQuery(ExternalDetectionMapper.BuildSliceSpec(layout));

        var expected = string.Join(',', layout.Slices.Select(s => $"{s.X}x{s.Y}"));
        Assert.Equal(expected, query);
    }

    [Fact]
    public void ResponseJson_MatchesTheDocumentedContractShape()
    {
        const string json = """
        [
          { "box": { "x1": 100.5, "y1": 150.0, "x2": 200.0, "y2": 300.5 },
            "class": 0, "name": "person", "confidence": 0.92 }
        ]
        """;

        var results = JsonSerializer.Deserialize<List<ExternalDetectionResult>>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var r = Assert.Single(results!);
        Assert.Equal(100.5, r.Box.X1);
        Assert.Equal(300.5, r.Box.Y2);
        Assert.Equal(0, r.Class);
        Assert.Equal("person", r.Name);
        Assert.Equal(0.92, r.Confidence);
    }
}
