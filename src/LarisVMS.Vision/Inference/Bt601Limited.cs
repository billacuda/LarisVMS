namespace LarisVMS.Vision.Inference;

/// <summary>
/// BT.601 limited ("studio") range YUV(0-255) → RGB(0-255). The near-universal case for camera
/// sub-streams at ≤720p. One source of truth so <see cref="OnnxPreprocessHead"/>'s GPU matrix and
/// <see cref="Nv12Ops"/>'s CPU path (eager snapshot crop, debug images) never drift.
///
/// C = Y-16, D = U-128, E = V-128;
/// R = Kc*C + Kr*E;  G = Kc*C - Kgu*D - Kgv*E;  B = Kc*C + Kb*D.
/// </summary>
internal static class Bt601Limited
{
    public const float Kc = 1.164383f;   // 255 / 219
    public const float Kr = 1.596027f;
    public const float Kgu = 0.391762f;
    public const float Kgv = 0.812968f;
    public const float Kb = 2.017232f;

    /// <summary>Converts one YUV triple (each 0-255) to RGB bytes (each 0-255, clamped).</summary>
    public static (byte R, byte G, byte B) ToRgb(float y, float u, float v)
    {
        var c = (y - 16f) * Kc;
        var d = u - 128f;
        var e = v - 128f;
        return (
            Clamp(c + Kr * e),
            Clamp(c - Kgu * d - Kgv * e),
            Clamp(c + Kb * d));
    }

    private static byte Clamp(float x) => (byte)(x < 0f ? 0f : x > 255f ? 255f : x);
}
