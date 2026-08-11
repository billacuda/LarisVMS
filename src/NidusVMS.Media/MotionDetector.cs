namespace NidusVMS.Media;

/// <summary>
/// Pure frame-diff scoring — no process, no I/O, so it's exercised directly with synthetic byte
/// arrays in <c>NidusVMS.Tests</c> rather than needing a live camera to validate the arithmetic.
/// <see cref="MotionSession"/> wraps this with the actual ffmpeg pipe and per-zone hysteresis.
/// </summary>
public static class MotionDetector
{
    /// <summary>Fraction (0.0-1.0) of masked pixels whose grayscale value changed by more than
    /// <paramref name="pixelDeltaThreshold"/> between the two frames. Zero if the mask has no true
    /// pixels (an all-Ignore-masked zone, or a zone entirely off one edge of frame) rather than
    /// dividing by zero — such a zone can never register motion, which is correct: there's nothing
    /// left in it to watch.</summary>
    public static double Score(ReadOnlySpan<byte> previousFrame, ReadOnlySpan<byte> currentFrame, ReadOnlySpan<bool> mask, byte pixelDeltaThreshold)
    {
        if (previousFrame.Length != currentFrame.Length || previousFrame.Length != mask.Length)
            throw new ArgumentException("previousFrame, currentFrame, and mask must all be the same length.");

        var maskedCount = 0;
        var changedCount = 0;
        for (var i = 0; i < mask.Length; i++)
        {
            if (!mask[i]) continue;
            maskedCount++;
            var delta = Math.Abs(previousFrame[i] - currentFrame[i]);
            if (delta > pixelDeltaThreshold) changedCount++;
        }

        return maskedCount == 0 ? 0.0 : (double)changedCount / maskedCount;
    }
}
