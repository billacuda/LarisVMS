namespace LarisVMS.Vision.Inference;

/// <summary>How <see cref="HttpDetectionEngine"/> sends a frame to the external service — resolved
/// from <c>Detection.ExternalInferenceTransport</c>'s free-form string
/// (<c>CameraDetectionPipeline</c> parses it, the same place <c>DetectionBackend</c> itself is
/// parsed) via <see cref="TryParse"/>.</summary>
public enum ExternalInferenceTransport
{
    /// <summary>Today's only behaviour: the capture buffer is BGRA, JPEG-encoded before every
    /// request. Works against any service, any network — the safe default.</summary>
    Jpeg,

    /// <summary>The capture buffer is nv12 (nothing sent needs a JPEG codec on either side) — sent as
    /// SideGlance's <c>pixels_yuv420sp</c> raw-pixel layout. ~10x JPEG's payload size, so only sane
    /// on a loopback or LAN service that has actually advertised <c>pixels_yuv420sp</c> in its own
    /// <c>GET /v1/models</c> <c>input_modes</c> (checked by the operator via the "Test connection"
    /// button, not auto-negotiated here — see this type's own file header for why).</summary>
    PixelsYuv420,

    /// <summary>The capture buffer stays BGRA (no ffmpeg/pipeline change at all versus "Jpeg") but is
    /// sent raw instead of JPEG-encoded — SideGlance's <c>pixels_bgra32</c> layout. Removes the JPEG
    /// encode/decode pair at ~4x JPEG's payload size (versus ~10x for nv12); a middle-ground reference
    /// mode, most useful on loopback where bandwidth is free but nv12's colour-matrix negotiation is
    /// unwanted complexity.</summary>
    PixelsBgra,
}

public static class ExternalInferenceTransportExtensions
{
    /// <summary>Parses <c>Detection.ExternalInferenceTransport</c>'s stored string. "Auto" and any
    /// unrecognized value both resolve to <see cref="ExternalInferenceTransport.Jpeg"/> — real
    /// capability-based auto-negotiation (probing the service's own <c>input_modes</c> at connect
    /// time and picking accordingly) is deliberately not implemented yet, so "Auto" is honestly just
    /// today's safe default under a name that leaves room for that later without another migration.
    /// Case-insensitive, matching every other Detection.* enum-like string in this codebase.</summary>
    public static ExternalInferenceTransport Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "pixelsyuv420" => ExternalInferenceTransport.PixelsYuv420,
        "pixelsbgra" => ExternalInferenceTransport.PixelsBgra,
        _ => ExternalInferenceTransport.Jpeg, // "jpeg", "auto", blank, or anything unrecognized
    };
}
