using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

/// <summary>
/// Covers <see cref="ExternalInferenceProbe.BuildResult"/> — the pure parse half of the
/// AI-detection settings page's "Test connection" button — against canned service responses, the
/// same no-HTTP approach the rest of this project's parsing helpers use.
/// </summary>
public class ExternalInferenceProbeParseTests
{
    private const string HealthJson = """
    { "status": "ok", "version": "1.4.2", "backend": "cuda",
      "models_ready": ["yolov8n", "rtdetr-l"], "models_failed": [] }
    """;

    [Fact]
    public void ParsesHealthAndModelListFromTheObjectFormedResponse()
    {
        const string modelsJson = """
        { "models": [
            { "name": "yolov8n", "status": "ready", "decoder": "yolo", "inputSize": 640, "classCount": 80 },
            { "name": "rtdetr-l", "status": "ready", "decoder": "rtdetr", "inputSize": 640, "classCount": 80 }
        ] }
        """;

        var result = ExternalInferenceProbe.BuildResult(HealthJson, modelsJson);

        Assert.Null(result.Error);
        Assert.Equal("ok", result.Health!.Status);
        Assert.Equal("1.4.2", result.Health.Version);
        Assert.Equal(2, result.Models!.Count);
        Assert.Equal("yolov8n", result.Models[0].Name);
        Assert.Equal(640, result.Models[0].InputSize);
        Assert.Equal(80, result.Models[0].ClassCount);
    }

    [Fact]
    public void AcceptsABareTopLevelModelArray()
    {
        const string modelsJson = """
        [ { "name": "m1", "inputSize": 512 }, { "name": "m2", "inputSize": 640 } ]
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
        var result = ExternalInferenceProbe.BuildResult("<html>404</html>", """[ { "name": "m1", "inputSize": 640 } ]""");

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
}
