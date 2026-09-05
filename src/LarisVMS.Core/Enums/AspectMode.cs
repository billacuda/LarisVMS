namespace LarisVMS.Core.Enums;

/// <summary>Detection/hardware-acceleration overhaul, pass 1: how a camera's own native (Sub-stream)
/// aspect ratio is fitted into D-FINE's square network input, replacing the old behavior of decoding
/// every camera to one fixed global resolution and stretching it into the model's input square
/// regardless of shape.</summary>
public enum AspectMode
{
    /// <summary>Preserve the camera's own aspect ratio, padding with black bars to fill the square —
    /// keeps the model at (or near) its trained input geometry for every camera shape, at the cost of
    /// some wasted pad pixels for a very wide or very tall camera. The default.</summary>
    Letterbox = 0,

    /// <summary>Independently scale each axis to fill the square exactly, distorting a non-square
    /// camera's geometry — today's pre-pass-1 behavior, kept so the change is bisectable.</summary>
    Stretch = 1,

    /// <summary>Reserved for a future pass — classify cameras into a small set of non-square network
    /// shapes (panoramic/portrait/standard) instead of always padding to one square. Not implemented:
    /// motion-guided native-scale tiling (see the plan's pass 3b, since removed) turned out to answer
    /// the panoramic-distortion problem this was meant to solve first, before <see cref="Slice"/>
    /// answered it better still. LarisVMS.Vision.Inference.InferenceProfile keeps this value so the
    /// option can be revisited if that approach disappoints — nothing implements it today, and
    /// requesting it throws.</summary>
    AspectMatched = 2,

    /// <summary>Detection/hardware-acceleration overhaul, pass 4 (frame slicing): scales the camera's
    /// own short edge to the network size and the long edge proportionally, then cuts the result into
    /// <c>N = max(2, ceil(longEdge / networkSize))</c> overlapping square slices, each run through the
    /// detector at full resolution and merged back into one detection list
    /// (<see cref="LarisVMS.Vision.Inference.SliceLayout"/>/<c>SliceMerge</c>) — the answer to a wide
    /// or tall camera losing small/distant objects to <see cref="Letterbox"/>'s black-bar padding and
    /// the resulting 2-3x downscale. Unlike <see cref="Letterbox"/>/<see cref="Stretch"/>, a Slice
    /// camera's own <see cref="LarisVMS.Vision.Inference.InferenceProfile"/> is never built — its
    /// capture buffer isn't square, so <c>InferenceProfile.Create</c> throws for this value; the whole
    /// per-camera geometry lives in <c>SliceLayout</c> instead. Multiplies inference work by N — see
    /// Admin &gt; Settings &gt; Detection's own help text for the capacity trade-off.</summary>
    Slice = 3,
}
