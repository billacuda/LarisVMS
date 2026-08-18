using Microsoft.AspNetCore.Http;
using LarisVMS.Web.Middleware;

namespace LarisVMS.Tests;

/// <summary>
/// The path classification behind PortSegmentationMiddleware — getting this list wrong in either
/// direction is a real problem: too broad and management pages leak onto the media port, too narrow
/// and live/playback silently breaks once a custom port is configured.
/// </summary>
public class MediaRoutesTests
{
    [Theory]
    [InlineData("/live/11111111-1111-1111-1111-111111111111")]
    [InlineData("/playback-segment/11111111-1111-1111-1111-111111111111/42")]
    [InlineData("/playback-thumbnail/11111111-1111-1111-1111-111111111111")]
    [InlineData("/playback-thumbnail/11111111-1111-1111-1111-111111111111/latest")]
    [InlineData("/export-download/11111111-1111-1111-1111-111111111111")]
    [InlineData("/api/cameras/11111111-1111-1111-1111-111111111111/snapshot")]
    public void RecognizesEveryMediaRoute(string path)
    {
        Assert.True(MediaRoutes.IsMediaPath(new PathString(path)));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Cameras")]
    [InlineData("/Admin/Settings/Security")]
    [InlineData("/api/dashboard")]
    [InlineData("/api/preferences")]
    [InlineData("/api/exports")]
    [InlineData("/api/timeline")]
    [InlineData("/health")]
    // Same /api/cameras/ prefix as the snapshot route, but these two are JSON polling endpoints with
    // no trailing dynamic segment — the exact case the plain-prefix approach would have gotten wrong.
    [InlineData("/api/cameras/motion-state")]
    [InlineData("/api/cameras/detection-state")]
    public void DoesNotFlagManagementRoutes(string path)
    {
        Assert.False(MediaRoutes.IsMediaPath(new PathString(path)));
    }

    [Fact]
    public void DoesNotFalselyMatchAPathThatOnlyLooksSimilar()
    {
        // "/liveness" starts with "/live" as raw characters but StartsWithSegments requires a
        // segment boundary, not just a string prefix — a naive path.StartsWith("/live") would
        // wrongly match this.
        Assert.False(MediaRoutes.IsMediaPath(new PathString("/liveness")));
    }
}
