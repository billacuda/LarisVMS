namespace LarisVMS.Vision.Capture;

/// <summary>
/// Configuration for one camera's <see cref="VisionSession"/> — always the Sub stream's RTSP URI
/// (already credential-injected by NodeWorker the same way ReconcileMotion/ReconcileLiveSub build
/// theirs), and a single fixed decode resolution. Unlike aitest's FrameReader/StreamProfile, there
/// is no profile-switching concept here: detection always watches one stream at one resolution.
/// </summary>
public sealed record VisionSessionOptions(
    string FfmpegPath,
    string RtspUri,
    int Width = 1280,
    int Height = 720,
    /// <summary>ffmpeg -hwaccel value, e.g. "cuda". Null decodes on the CPU. Only "cuda" gets the
    /// GPU-hybrid scale_cuda treatment (see VisionSession.StartFfmpeg) — the only pairing actually
    /// proven end-to-end (in aitest, against real cameras); any other value still requests hardware
    /// decode from ffmpeg but falls back to a plain CPU scale filter afterward.</summary>
    string? HardwareAcceleration = null,
    int StalledThresholdSeconds = 30,
    int PollIntervalSeconds = 5);
