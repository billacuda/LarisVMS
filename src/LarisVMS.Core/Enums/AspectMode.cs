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
    /// motion-guided native-scale tiling (see the plan's pass 3b) turned out to answer the panoramic-
    /// distortion problem this was meant to solve, without feeding D-FINE input shapes it was never
    /// trained on. LarisVMS.Vision.Inference.InferenceProfile keeps this value so the option can be
    /// revisited if that approach disappoints — nothing implements it today, and requesting it throws.</summary>
    AspectMatched = 2,
}
