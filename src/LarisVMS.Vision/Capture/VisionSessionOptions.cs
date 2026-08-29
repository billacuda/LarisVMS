namespace LarisVMS.Vision.Capture;

/// <summary>Detection/hardware-acceleration overhaul, pass 1: the aspect-preserving pre-pad scale
/// target and pad offsets VisionSession's own ffmpeg filter chain needs to letterbox a camera's real
/// aspect ratio into the network's square input — computed once from InferenceProfile
/// (LarisVMS.Vision.Inference) and passed here as plain ints rather than the profile itself, so this
/// capture-layer type doesn't need to reference the inference layer for a handful of numbers.</summary>
public readonly record struct LetterboxGeometry(int ScaledWidth, int ScaledHeight, int PadLeft, int PadTop);

/// <summary>
/// Configuration for one camera's <see cref="VisionSession"/> — always the Sub stream's RTSP URI
/// (already credential-injected by NodeWorker the same way ReconcileMotion/ReconcileLiveSub build
/// theirs).
/// </summary>
public sealed record VisionSessionOptions(
    string FfmpegPath,
    string RtspUri,
    /// <summary>The captured frame's own dimensions — as of pass 1, always the detection engine's
    /// network input size (InferenceProfile.NetworkWidth/NetworkHeight), so ffmpeg emits frames
    /// already at the exact tensor input size and DFineEngine.Preprocess never resizes. Before this
    /// pass this was a fixed global decode resolution (1280x720) unrelated to the model's own 640x640
    /// input, with a separate SKBitmap.Resize bridging the two.</summary>
    int Width = 640,
    int Height = 640,
    /// <summary>ffmpeg -hwaccel value, e.g. "cuda". Null decodes on the CPU. Only "cuda" gets the
    /// GPU-hybrid scale_cuda treatment (see VisionSession.StartFfmpeg) — the only pairing actually
    /// proven end-to-end (in aitest, against real cameras); any other value still requests hardware
    /// decode from ffmpeg but falls back to a plain CPU scale filter afterward.</summary>
    string? HardwareAcceleration = null,
    /// <summary>Non-null only for AspectMode.Letterbox — see LetterboxGeometry's own doc comment.
    /// Null for Stretch, whose plain scale=Width:Height filter needs no separate pre-pad step.</summary>
    LetterboxGeometry? Letterbox = null,
    int StalledThresholdSeconds = 30,
    int PollIntervalSeconds = 5);
