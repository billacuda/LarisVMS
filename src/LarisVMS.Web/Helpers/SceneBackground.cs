namespace LarisVMS.Web.Helpers;

/// <summary>Rules for a picture uploaded to show under a camera's scene heatmap.</summary>
public static class SceneBackground
{
    /// <summary>How far a picture's shape (width / height) may be from the camera's.</summary>
    public const double ShapeTolerance = 0.05;

    /// <summary>Whether a <paramref name="width"/> x <paramref name="height"/> picture is the same
    /// shape as the camera's <paramref name="cameraWidth"/> x <paramref name="cameraHeight"/> picture,
    /// within <see cref="ShapeTolerance"/> — the heat is laid over it edge to edge, so anything else
    /// would put it in the wrong places.</summary>
    public static bool ShapeMatches(int width, int height, int cameraWidth, int cameraHeight)
    {
        if (width <= 0 || height <= 0 || cameraWidth <= 0 || cameraHeight <= 0) return false;
        var ratio = (double)width / height / ((double)cameraWidth / cameraHeight);
        return Math.Abs(ratio - 1) <= ShapeTolerance;
    }
}
