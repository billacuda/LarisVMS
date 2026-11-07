using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using LarisVMS.Web.Middleware;

namespace LarisVMS.Tests;

/// <summary>Keeps Help/16-api.md complete and the public routes behind their guards. These read the
/// source, because the routes are declared inline in Program.cs.</summary>
public partial class ApiDocsTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LarisVMS.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }

    private static string WebFile(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot(), "src", "LarisVMS.Web", .. parts]));

    private static readonly Dictionary<string, string> Prefixes = new()
    {
        ["apiRoutes"] = "/api/v1",
        ["ptzRoutes"] = "/api/v1/cameras/{cameraId}/ptz",
        ["playbackRoutes"] = "/api/v1/cameras/{cameraId}",
    };

    private static string Clean(string path) => path.Replace(":guid", "");

    private static List<string> PublicRoutes()
    {
        var program = WebFile("Program.cs");
        var routes = new List<string>();

        foreach (Match m in SharedRoute().Matches(program))
            routes.Add($"{m.Groups[2].Value.ToUpperInvariant()} {Prefixes[m.Groups[1].Value]}{Clean(m.Groups[3].Value)}");

        foreach (Match m in DirectRoute().Matches(program + WebFile("Services", "ManualRecordingApi.cs")))
            routes.Add($"{m.Groups[1].Value.ToUpperInvariant()} {Clean(m.Groups[2].Value)}");

        return routes;
    }

    [Fact]
    public void FindsThePublicRoutes()
    {
        var routes = PublicRoutes();

        Assert.Contains("GET /api/v1/status", routes);
        Assert.Contains("POST /api/v1/cameras/{cameraId}/ptz/move", routes);
        Assert.Contains("GET /api/v1/cameras/{cameraId}/timeline", routes);
        Assert.Contains("POST /api/v1/recording/start", routes);
        Assert.Contains("DELETE /api/v1/exports/{jobId}", routes);
    }

    [Fact]
    public void EveryPublicRouteIsDocumented()
    {
        var docs = WebFile("Help", "16-api.md");
        var missing = PublicRoutes().Where(r => !docs.Contains("### " + r + "\n") && !docs.Contains("### " + r + "\r\n")).ToList();

        Assert.True(missing.Count == 0, "Missing from 16-api.md: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryDocumentedRouteExists()
    {
        var routes = PublicRoutes();
        var stale = DocumentedRoute().Matches(WebFile("Help", "16-api.md")).Select(m => m.Groups[1].Value.TrimEnd())
            .Where(r => r != "GET /health" && !routes.Contains(r)).ToList();

        Assert.True(stale.Count == 0, "In 16-api.md but not in the code: " + string.Join(", ", stale));
    }

    [Theory]
    [InlineData("/api/v1/cameras/00000000-0000-0000-0000-000000000001/snapshot", true)]
    [InlineData("/api/v1/cameras/00000000-0000-0000-0000-000000000001/thumbnail/latest", true)]
    [InlineData("/api/v1/exports/items/00000000-0000-0000-0000-000000000001/download", true)]
    [InlineData("/api/v1/status", false)]
    [InlineData("/api/v1/cameras/00000000-0000-0000-0000-000000000001/timeline", false)]
    public void MediaCopiesFollowTheMediaAllowList(string path, bool isMedia)
    {
        Assert.Equal(isMedia, MediaRoutes.IsMediaPath(new PathString(path)));
    }

    [GeneratedRegex(@"\b(apiRoutes|ptzRoutes|playbackRoutes)\.(Get|Post|Delete)\(""([^""]+)""")]
    private static partial Regex SharedRoute();

    [GeneratedRegex(@"\.Map(Get|Post|Delete)\(""(/api/v1[^""]*)""")]
    private static partial Regex DirectRoute();

    [GeneratedRegex(@"^### ((?:GET|POST|DELETE) /\S*)", RegexOptions.Multiline)]
    private static partial Regex DocumentedRoute();
}
