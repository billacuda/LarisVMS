using LarisVMS.Core.Dtos;
using SkiaSharp;
using YoloDotNet.Models;

namespace LarisVMS.Vision.Inference;

/// <summary>
/// Pure translation between the external HTTP inference contract (<see cref="ExternalDetectRequest"/>
/// / <see cref="ExternalDetectionResult"/>) and this codebase's own detection shape — the same job
/// <see cref="DFineDecoder"/> does for D-FINE's raw tensors, and verified the same way (fixed inputs
/// in, expected <see cref="SKRectI"/>s out), so <see cref="HttpDetectionEngine"/> keeps no geometry
/// logic of its own and every downstream consumer (ByteTracker, MovementClassifier, CocoCategoryMap,
/// every report DTO) is untouched.
///
/// Boxes come back from here in <see cref="InferenceProfile.SourceWidth"/> ×
/// <see cref="InferenceProfile.SourceHeight"/> integer-pixel space — identical to
/// <see cref="DFineDecoder.Decode"/>'s own output contract — regardless of whether the request was
/// letterboxed/stretched to a single square or sliced.
/// </summary>
public static class ExternalDetectionMapper
{
    /// <summary>The tile plan for a sliced request, straight off <paramref name="layout"/>. The
    /// external service applies these against the submitted image with no scaling and returns boxes
    /// in full-frame (capture-pixel) space with seam-straddling detections already merged, so
    /// <see cref="MapSlicedResponse"/> only has to undo <see cref="SliceLayout"/>'s uniform,
    /// unpadded capture→source scale.</summary>
    public static ExternalSliceSpec BuildSliceSpec(SliceLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var tiles = new List<ExternalSliceTile>(layout.Slices.Count);
        foreach (var t in layout.Slices) tiles.Add(new ExternalSliceTile(t.X, t.Y));
        return new ExternalSliceSpec(layout.CaptureWidth, layout.CaptureHeight, tiles);
    }

    /// <summary>Maps a plain (non-sliced) response — boxes in the submitted square's own pixel space,
    /// i.e. <paramref name="profile"/>.NetworkWidth × NetworkHeight — back through the profile's
    /// letterbox/stretch inverse into source-frame pixels. The xyxy→normalized-cxcywh step exists
    /// purely because <see cref="InferenceProfile.MapBoxToSource"/> speaks the same cxcywh D-FINE
    /// emits; the rest is <see cref="DFineDecoder"/>'s tail verbatim (multiply out, clamp, drop
    /// degenerate).</summary>
    public static List<ObjectDetection> MapPlainResponse(
        IReadOnlyList<ExternalDetectionResult> results, InferenceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var mapped = new List<ObjectDetection>(results?.Count ?? 0);
        if (results is null) return mapped;

        foreach (var r in results)
        {
            if (r?.Box is not { } box) continue;

            var cx = (box.X1 + box.X2) / 2.0 / profile.NetworkWidth;
            var cy = (box.Y1 + box.Y2) / 2.0 / profile.NetworkHeight;
            var w = (box.X2 - box.X1) / profile.NetworkWidth;
            var h = (box.Y2 - box.Y1) / profile.NetworkHeight;

            var (nx0, ny0, nx1, ny1) = profile.MapBoxToSource(cx, cy, w, h);
            if (BuildRect(nx0 * profile.SourceWidth, ny0 * profile.SourceHeight,
                    nx1 * profile.SourceWidth, ny1 * profile.SourceHeight,
                    profile.SourceWidth, profile.SourceHeight) is { } rect)
            {
                mapped.Add(ToDetection(r, rect));
            }
        }
        return mapped;
    }

    /// <summary>Maps a sliced response — the external service already merged across seams and reports
    /// every box in full-frame (capture-pixel) space, so this is a plain uniform down-scale from the
    /// <paramref name="layout"/> capture buffer into source pixels (no pad to undo — see
    /// <see cref="SliceLayout"/>'s own doc comment), then the same clamp/drop-degenerate tail.</summary>
    public static List<ObjectDetection> MapSlicedResponse(
        IReadOnlyList<ExternalDetectionResult> results, SliceLayout layout, int sourceWidth, int sourceHeight)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var mapped = new List<ObjectDetection>(results?.Count ?? 0);
        if (results is null) return mapped;

        var scaleX = (double)sourceWidth / layout.CaptureWidth;
        var scaleY = (double)sourceHeight / layout.CaptureHeight;

        foreach (var r in results)
        {
            if (r?.Box is not { } box) continue;
            if (BuildRect(box.X1 * scaleX, box.Y1 * scaleY, box.X2 * scaleX, box.Y2 * scaleY,
                    sourceWidth, sourceHeight) is { } rect)
            {
                mapped.Add(ToDetection(r, rect));
            }
        }
        return mapped;
    }

    private static SKRectI? BuildRect(double x0, double y0, double x1, double y1, int frameW, int frameH)
    {
        var left = (int)Math.Round(Math.Clamp(x0, 0, frameW));
        var top = (int)Math.Round(Math.Clamp(y0, 0, frameH));
        var right = (int)Math.Round(Math.Clamp(x1, 0, frameW));
        var bottom = (int)Math.Round(Math.Clamp(y1, 0, frameH));
        if (right <= left || bottom <= top) return null; // degenerate — nothing to report
        return new SKRectI(left, top, right, bottom);
    }

    private static ObjectDetection ToDetection(ExternalDetectionResult r, SKRectI rect) => new()
    {
        Label = new LabelModel { Index = r.Class, Name = string.IsNullOrWhiteSpace(r.Name) ? "object" : r.Name },
        Confidence = r.Confidence,
        BoundingBox = rect,
        Tail = [],
    };
}
