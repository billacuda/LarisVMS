using System.Text.Json;
using LarisVMS.Core.Dtos;
using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

/// <summary>
/// Covers <see cref="ExternalInferenceProbe.BuildResult"/> — the pure parse half of the
/// AI-detection settings page's "Test connection" button — against canned service responses.
///
/// Fixtures here use SideGlance's *actual* wire shapes: snake_case field names
/// (<c>input_size</c>/<c>class_count</c>/<c>batch_mode</c>/<c>models_ready</c>/<c>models_failed</c>)
/// and integer <c>models_ready</c>/<c>models_failed</c> counts — not the camelCase-with-string-array
/// shapes an earlier version of this file used, which encoded the exact mismatch between
/// <see cref="ExternalModelInfo"/>/<see cref="ExternalHealthResponse"/> and SideGlance's real
/// <c>ModelInfoDto</c>/<c>HealthDto</c> as "expected" — so the bug shipped with a green test suite.
/// See those two records' own doc comments for the full story.
/// </summary>
public class ExternalInferenceProbeParseTests
{
    private const string HealthJson = """
    { "status": "ok", "version": "1.4.2", "backend": "cuda",
      "models_ready": 2, "models_failed": 0 }
    """;

    [Fact]
    public void ParsesHealthAndModelListFromTheObjectFormedResponse()
    {
        const string modelsJson = """
        { "models": [
            { "name": "yolov8n", "status": "ready", "decoder": "yolo", "input_size": 640, "class_count": 80 },
            { "name": "rtdetr-l", "status": "ready", "decoder": "rtdetr", "input_size": 640, "class_count": 80 }
        ] }
        """;

        var result = ExternalInferenceProbe.BuildResult(HealthJson, modelsJson);

        Assert.Null(result.Error);
        Assert.Equal("ok", result.Health!.Status);
        Assert.Equal("1.4.2", result.Health.Version);
        Assert.Equal(2, result.Health.ModelsReady);
        Assert.Equal(0, result.Health.ModelsFailed);
        Assert.Equal(2, result.Models!.Count);
        Assert.Equal("yolov8n", result.Models[0].Name);
        Assert.Equal(640, result.Models[0].InputSize);
        Assert.Equal(80, result.Models[0].ClassCount);
    }

    [Fact]
    public void AcceptsABareTopLevelModelArray()
    {
        const string modelsJson = """
        [ { "name": "m1", "input_size": 512 }, { "name": "m2", "input_size": 640 } ]
        """;

        var result = ExternalInferenceProbe.BuildResult(null, modelsJson);

        Assert.Null(result.Error);
        Assert.Null(result.Health); // no /healthz body supplied
        Assert.Equal(2, result.Models!.Count);
        Assert.Equal(512, result.Models[0].InputSize);
    }

    [Fact]
    public void EmptyModelListIsAnError()
    {
        var result = ExternalInferenceProbe.BuildResult(HealthJson, """{ "models": [] }""");

        Assert.NotNull(result.Error);
        Assert.Null(result.Models);
        Assert.NotNull(result.Health); // health still surfaced for context
    }

    [Fact]
    public void MalformedModelBodyIsAnErrorNotAnException()
    {
        var result = ExternalInferenceProbe.BuildResult(null, "not json at all");

        Assert.NotNull(result.Error);
        Assert.Null(result.Models);
    }

    [Fact]
    public void MalformedHealthBodyIsIgnoredWhenModelsAreFine()
    {
        var result = ExternalInferenceProbe.BuildResult("<html>404</html>", """[ { "name": "m1", "input_size": 640 } ]""");

        Assert.Null(result.Error);
        Assert.Null(result.Health);
        Assert.Single(result.Models!);
    }

    [Fact]
    public void MissingInputSizeDefaultsTo640()
    {
        var result = ExternalInferenceProbe.BuildResult(null, """[ { "name": "m1" } ]""");

        Assert.Null(result.Error);
        Assert.Equal(640, result.Models![0].InputSize);
    }

    /// <summary>Regression test for the bug these two records' doc comments describe: a
    /// health/models body shaped exactly the way SideGlance's own serializer
    /// (<c>SnakeCaseLower</c> naming, integers for the two counts) actually emits it must
    /// deserialize cleanly through <see cref="ExternalHealthResponse"/>/<see cref="ExternalModelInfo"/>
    /// with every field populated — not silently drop to defaults or throw and get swallowed. This
    /// doesn't import SideGlance's assembly (separate solution, separate license boundary — see
    /// SideGlance's own CLAUDE.md on why nothing is shared between the two repos); it reproduces the
    /// wire shape with the same <see cref="JsonNamingPolicy.SnakeCaseLower"/> SideGlance's
    /// <c>SideGlanceJson.Options</c> uses, applied to a plain object shaped like SideGlance's real
    /// <c>ModelInfoDto</c>/<c>HealthDto</c>.</summary>
    [Fact]
    public void RoundTripsThroughSideGlancesActualNamingPolicy()
    {
        var sideGlanceOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        // Shaped like SideGlance's ModelInfoDto (Models/Responses.cs) and HealthDto — PascalCase
        // .NET property names, serialized with the naming policy SideGlance's server actually uses,
        // not hand-written snake_case JSON that could itself drift from the real serializer.
        var modelInfo = new { Name = "yolo11s-coco", Status = "ready", Decoder = "ultralytics",
            InputSize = 1280, ClassCount = 80, BatchMode = "sequential",
            InputModes = new[] { "image", "pixels_yuv420sp", "pixels_bgra32" } };
        var health = new { Status = "ok", Version = "1.6.0", Backend = "cuda",
            ModelsReady = 1, ModelsFailed = 0 };

        var modelsJson = $$"""{ "models": [ {{JsonSerializer.Serialize(modelInfo, sideGlanceOptions)}} ] }""";
        var healthJson = JsonSerializer.Serialize(health, sideGlanceOptions);

        var result = ExternalInferenceProbe.BuildResult(healthJson, modelsJson);

        Assert.Null(result.Error);
        Assert.Equal(1280, result.Models![0].InputSize); // the field that silently stuck at 640 before the fix
        Assert.Equal(80, result.Models[0].ClassCount);
        Assert.Equal("sequential", result.Models[0].BatchMode);
        Assert.Contains("pixels_yuv420sp", result.Models[0].InputModes!);
        Assert.NotNull(result.Health); // used to be null: the type mismatch threw and was swallowed
        Assert.Equal(1, result.Health!.ModelsReady);
        Assert.Equal(0, result.Health.ModelsFailed);
    }
}
