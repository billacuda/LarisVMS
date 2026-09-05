using LarisVMS.Core.Enums;

namespace LarisVMS.Vision.Inference;

/// <summary>
/// Detection/hardware-acceleration overhaul, pass 1. Precomputes the forward (source → network) and
/// inverse (network → source) geometry for one camera's inference pipeline, given its own native
/// Sub-stream aspect ratio and the deployment's chosen <see cref="AspectMode"/>.
///
/// "Source" here means the camera's own real aspect ratio at whatever resolution it's decoded for
/// detection (today, always the Sub stream) — the space every live-overlay box and snapshot crop
/// needs to land in, since that's what the actual video looks like. "Network" is D-FINE's own square
/// input the ONNX graph actually consumes. Before this pass, VisionSession always decoded to one
/// fixed global resolution (1280x720) and DFineEngine.Preprocess stretched that non-aspect-preserving
/// into 640x640 with a separate SKBitmap.Resize call — this class is what lets both of those steps be
/// replaced with ffmpeg producing the network-sized frame directly (see VisionSession's own
/// BuildFfmpegArgs), with geometry correctness preserved regardless of a camera's real shape.
///
/// Pure and immutable once constructed — safe to build once per camera pipeline and share between
/// VisionSession (to pick ffmpeg's own scale/pad filter target) and DFineDecoder (to map a detected
/// box in normalized model-space back to normalized source-frame coordinates).
/// </summary>
public sealed class InferenceProfile
{
    public const int DefaultNetworkSize = 640;

    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public int NetworkWidth { get; }
    public int NetworkHeight { get; }
    public AspectMode Mode { get; }

    /// <summary>The aspect-preserving intermediate size ffmpeg's own -vf scale target should use
    /// ahead of padding, for Letterbox. Equals NetworkWidth/NetworkHeight for Stretch — there is no
    /// separate pre-pad scale step, since a plain scale=NetworkWidth:NetworkHeight already does the
    /// whole job in one filter.</summary>
    public int ScaledWidth { get; }
    public int ScaledHeight { get; }

    /// <summary>Pixels of black padding ffmpeg's own -vf pad target should place on the left/top —
    /// the same amount sits on the right/bottom, since padding is always centered. Zero for Stretch.</summary>
    public int PadLeft { get; }
    public int PadTop { get; }

    private InferenceProfile(int sourceWidth, int sourceHeight, int networkWidth, int networkHeight,
        AspectMode mode, int scaledWidth, int scaledHeight, int padLeft, int padTop)
    {
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        NetworkWidth = networkWidth;
        NetworkHeight = networkHeight;
        Mode = mode;
        ScaledWidth = scaledWidth;
        ScaledHeight = scaledHeight;
        PadLeft = padLeft;
        PadTop = padTop;
    }

    public static InferenceProfile Create(int sourceWidth, int sourceHeight, AspectMode mode, int networkSize = DefaultNetworkSize)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "Source dimensions must be positive.");
        if (networkSize <= 0 || networkSize % 32 != 0)
            throw new ArgumentOutOfRangeException(nameof(networkSize), "Network size must be a positive multiple of 32.");

        return mode switch
        {
            AspectMode.Stretch => new InferenceProfile(sourceWidth, sourceHeight, networkSize, networkSize, mode,
                scaledWidth: networkSize, scaledHeight: networkSize, padLeft: 0, padTop: 0),

            AspectMode.Letterbox => CreateLetterbox(sourceWidth, sourceHeight, networkSize),

            AspectMode.AspectMatched => throw new NotSupportedException(
                "AspectMode.AspectMatched is reserved for a future pass — see the enum's own doc comment."),

            AspectMode.Slice => throw new NotSupportedException(
                "AspectMode.Slice has no single InferenceProfile — its capture buffer isn't square. " +
                "Use SliceLayout.Create instead; CameraDetectionPipeline branches on the mode before " +
                "ever reaching here."),

            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };
    }

    private static InferenceProfile CreateLetterbox(int sourceWidth, int sourceHeight, int networkSize)
    {
        var rawScale = Math.Min((double)networkSize / sourceWidth, (double)networkSize / sourceHeight);
        var scaledWidth = (int)Math.Round(sourceWidth * rawScale);
        var scaledHeight = (int)Math.Round(sourceHeight * rawScale);

        // Even only — 4:2:0 chroma subsampling requires it, and ffmpeg's scale/pad filters reject odd
        // targets for yuv420p-family formats. Never below 2px (ffmpeg rejects a zero/negative target
        // outright), matching SnapshotImageCapture.ComputeCropRect's own "never degenerate" convention
        // elsewhere in this codebase.
        scaledWidth = Math.Max(2, scaledWidth - scaledWidth % 2);
        scaledHeight = Math.Max(2, scaledHeight - scaledHeight % 2);

        // networkSize is always even (a multiple of 32) and scaledWidth/Height are now forced even
        // too, so this difference is always exactly divisible by 2 — no fractional pad pixel to bias
        // toward one side.
        var padLeft = (networkSize - scaledWidth) / 2;
        var padTop = (networkSize - scaledHeight) / 2;

        return new InferenceProfile(sourceWidth, sourceHeight, networkSize, networkSize, AspectMode.Letterbox,
            scaledWidth, scaledHeight, padLeft, padTop);
    }

    /// <summary>
    /// Maps one normalized (0-1) D-FINE box in cxcywh form — exactly as `pred_boxes` emits it — to
    /// normalized (0-1) xyxy coordinates in *source*-frame space, undoing this profile's own forward
    /// scale+pad. Source-normalized rather than source-pixel, so a caller never needs SourceWidth/
    /// SourceHeight again once it has a box back from here: multiply by whatever frame it's actually
    /// rendering against (the live overlay's own video element, a Main-stream snapshot crop) to get
    /// real pixels there — the same reasoning VisionLiveDetectionBox's own normalized coordinates
    /// already use.
    ///
    /// Not clamped to [0,1] — a model prediction can legitimately land slightly outside the source
    /// frame (an object exiting past the camera's own edge) or, for Letterbox, entirely inside the
    /// pad bars, which naturally maps to a coordinate outside [0,1]. Clamping is the caller's own
    /// concern (DFineDecoder.Decode already clamps before building a pixel rect), so this stays a
    /// pure, unclamped geometry transform.
    /// </summary>
    public (double X0, double Y0, double X1, double Y1) MapBoxToSource(double cx, double cy, double w, double h)
    {
        var nx0 = (cx - w / 2.0) * NetworkWidth;
        var ny0 = (cy - h / 2.0) * NetworkHeight;
        var nx1 = (cx + w / 2.0) * NetworkWidth;
        var ny1 = (cy + h / 2.0) * NetworkHeight;

        var scaleX = (double)ScaledWidth / SourceWidth;
        var scaleY = (double)ScaledHeight / SourceHeight;

        var sx0 = (nx0 - PadLeft) / scaleX / SourceWidth;
        var sy0 = (ny0 - PadTop) / scaleY / SourceHeight;
        var sx1 = (nx1 - PadLeft) / scaleX / SourceWidth;
        var sy1 = (ny1 - PadTop) / scaleY / SourceHeight;

        return (sx0, sy0, sx1, sy1);
    }

    /// <summary>Forward transform (the inverse of <see cref="MapBoxToSource"/>): a box in normalized
    /// (0-1) source-frame coordinates → integer xyxy pixels in the network-input buffer — i.e. where
    /// that box actually sits in the frame the detection engine received. Used to crop a snapshot
    /// straight out of that buffer (pass G). Clamped to the buffer, and (for Letterbox) to the real
    /// content rectangle so a margin can't pull pad bars into the crop.</summary>
    public (int X0, int Y0, int X1, int Y1) NormalizedSourceToNetworkPixels(double x0, double y0, double x1, double y1)
    {
        int Cx(double v) => (int)Math.Round(Math.Clamp(v * ScaledWidth + PadLeft, PadLeft, PadLeft + ScaledWidth));
        int Cy(double v) => (int)Math.Round(Math.Clamp(v * ScaledHeight + PadTop, PadTop, PadTop + ScaledHeight));
        return (Cx(x0), Cy(y0), Cx(x1), Cy(y1));
    }

    /// <summary>The real image rectangle inside the network buffer — the whole buffer for Stretch,
    /// the letterboxed content region (excluding pad bars) otherwise.</summary>
    public SkiaSharp.SKRectI ContentRect => new(PadLeft, PadTop, PadLeft + ScaledWidth, PadTop + ScaledHeight);
}
