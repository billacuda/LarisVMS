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
    /// <summary>The captured frame's own dimensions — as of pass 1 this is always the detection
    /// engine's network input size (InferenceProfile.NetworkWidth/NetworkHeight), so ffmpeg emits
    /// frames already at the exact tensor input size and the engine's Preprocess never resizes.
    /// Before that pass it was a fixed global decode resolution (1280x720) unrelated to the model's
    /// own input, with a separate SKBitmap.Resize bridging the two.</summary>
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
    /// <summary>Pass 4a: emit packed nv12 (<c>-pix_fmt nv12</c>, <c>W*H*3/2</c> bytes/frame) instead
    /// of BGRA. Set when the detection engine has the GPU preprocessing head merged in — ffmpeg then
    /// does no <c>swscale</c> colour conversion at all; the nv12→RGB step happens on the accelerator
    /// inside ONNX Runtime. The <c>scale_cuda</c>/<c>pad</c> filter chain is unchanged.</summary>
    bool Nv12Output = false,
    /// <summary>When &gt; 0, an <c>fps=N</c> filter is appended to the chain so the model sees at most
    /// N frames/sec — the ffmpeg process still decodes the Sub stream in real time, it just drops the
    /// surplus before they reach inference. Node only sends a value here when the stream's own rate
    /// is genuinely higher (never causing frame duplication). 0 = no filter.</summary>
    int FpsCap = 0,
    int StalledThresholdSeconds = 30,
    int PollIntervalSeconds = 5);
