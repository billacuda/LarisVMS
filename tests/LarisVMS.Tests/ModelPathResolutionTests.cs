using LarisVMS.Core.Enums;
using LarisVMS.Vision.Service;
using Microsoft.Extensions.Logging.Abstractions;

namespace LarisVMS.Tests;

/// <summary>
/// Covers CameraPipelineManager.ResolveModelPath — three tiers: an explicitly configured file that
/// exists always wins, then the exact filename DetectionModelCatalog expects for the resolved
/// (family, weights) selection, then an alphabetical glob fallback for a stale/hand-placed file
/// (the tier that let YOLOv9 keep silently running after this integration replaced it, back when
/// the glob was the *only* lookup).
/// </summary>
public class ModelPathResolutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "larisvms-model-tests-" + Guid.NewGuid().ToString("N"));

    private string NewModelsDirectory()
    {
        var dir = Path.Combine(_root, "models");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Resolve(string configuredPath, DetectionModelFamily family = DetectionModelFamily.DFine,
        DFineWeights dfineWeights = DFineWeights.Obj2Coco, YoloXSize yoloXSize = YoloXSize.S) =>
        CameraPipelineManager.ResolveModelPath(configuredPath, family, dfineWeights, yoloXSize, NullLogger.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void UsesTheConfiguredFileWhenItActuallyExists()
    {
        var dir = NewModelsDirectory();
        var configured = Path.Combine(dir, "model.onnx");
        File.WriteAllText(configured, "");
        // A second file that would win both the catalog lookup and the alphabetical fallback,
        // proving the explicit path is not merely being rediscovered by chance.
        File.WriteAllText(Path.Combine(dir, "dfine_s_obj2coco.onnx"), "");

        Assert.Equal(configured, Resolve(configured));
    }

    [Fact]
    public void UsesTheExactCatalogFilenameWhenBundled()
    {
        // The exact production failure this tier exists for: the configured name doesn't exist, but
        // fetch_dfine.py's own output filename does.
        var dir = NewModelsDirectory();
        var bundled = Path.Combine(dir, "dfine_s_obj2coco.onnx");
        File.WriteAllText(bundled, "");

        Assert.Equal(bundled, Resolve(Path.Combine(dir, "model.onnx")));
    }

    [Fact]
    public void PicksTheCatalogFilenameMatchingTheResolvedWeightsVariantEvenWhenAlphabeticallySecond()
    {
        // "obj2coco" sorts before "obj365" — proving this is a real catalog lookup, not secretly
        // still just the alphabetical fallback with extra steps.
        var dir = NewModelsDirectory();
        var obj2coco = Path.Combine(dir, "dfine_s_obj2coco.onnx");
        var obj365 = Path.Combine(dir, "dfine_s_obj365.onnx");
        File.WriteAllText(obj2coco, "");
        File.WriteAllText(obj365, "");

        Assert.Equal(obj365, Resolve(Path.Combine(dir, "model.onnx"), dfineWeights: DFineWeights.Obj365));
        Assert.Equal(obj2coco, Resolve(Path.Combine(dir, "model.onnx"), dfineWeights: DFineWeights.Obj2Coco));
    }

    [Fact]
    public void ResolvesTheMediumObj2CocoFilenameForThatWeightsVariant()
    {
        // dfine_m_obj2coco.onnx sorts before both small files — proving this is the catalog lookup
        // keyed on Obj2CocoMedium, not the alphabetical fallback.
        var dir = NewModelsDirectory();
        var medium = Path.Combine(dir, "dfine_m_obj2coco.onnx");
        var small = Path.Combine(dir, "dfine_s_obj2coco.onnx");
        File.WriteAllText(medium, "");
        File.WriteAllText(small, "");

        Assert.Equal(medium, Resolve(Path.Combine(dir, "model.onnx"), dfineWeights: DFineWeights.Obj2CocoMedium));
        Assert.Equal(small, Resolve(Path.Combine(dir, "model.onnx"), dfineWeights: DFineWeights.Obj2Coco));
    }

    [Fact]
    public void FallsBackToAlphabeticalGlobWhenTheExpectedCatalogFileIsMissing()
    {
        // A stale/hand-placed model with the wrong name — the last-resort tier, not the normal path.
        var dir = NewModelsDirectory();
        foreach (var name in new[] { "custom-a.onnx", "custom-b.onnx" })
            File.WriteAllText(Path.Combine(dir, name), "");

        var first = Resolve(Path.Combine(dir, "model.onnx"));
        var again = Resolve(Path.Combine(dir, "model.onnx"));

        Assert.Equal(Path.Combine(dir, "custom-a.onnx"), first);
        Assert.Equal(first, again); // stable across calls — a node must not switch models on restart
    }

    [Fact]
    public void IgnoresNonOnnxFilesInTheModelsDirectory()
    {
        // build-node.ps1 also drops a .LICENSE.txt beside each fetched model.
        var dir = NewModelsDirectory();
        File.WriteAllText(Path.Combine(dir, "dfine_s_obj2coco.onnx.LICENSE.txt"), "");
        File.WriteAllText(Path.Combine(dir, "readme.md"), "");
        var bundled = Path.Combine(dir, "dfine_s_obj2coco.onnx");
        File.WriteAllText(bundled, "");

        Assert.Equal(bundled, Resolve(Path.Combine(dir, "model.onnx")));
    }

    [Fact]
    public void ThrowsNamingTheExpectedFileWhenNoModelIsBundled()
    {
        var dir = NewModelsDirectory();

        var ex = Assert.Throws<FileNotFoundException>(() => Resolve(Path.Combine(dir, "model.onnx")));
        Assert.Contains(dir, ex.Message);
        Assert.Contains("dfine_s_obj2coco.onnx", ex.Message);
        Assert.Contains("fetch_dfine", ex.Message);
    }

    [Fact]
    public void ThrowsWhenTheModelsDirectoryDoesNotExistAtAll()
    {
        var missing = Path.Combine(_root, "nonexistent", "model.onnx");

        var ex = Assert.Throws<FileNotFoundException>(() => Resolve(missing));
        Assert.Contains("fetch_dfine", ex.Message);
    }

    [Fact]
    public void ResolvesARelativeConfiguredPathAgainstTheApplicationDirectory()
    {
        // Relative paths must not depend on the current working directory: this process is launched
        // by NodeWorker's supervisor, not from a shell whose cwd means anything.
        var appModels = Path.Combine(AppContext.BaseDirectory, "models");
        var created = !Directory.Exists(appModels);
        Directory.CreateDirectory(appModels);
        var bundled = Path.Combine(appModels, "relative-probe.onnx");
        File.WriteAllText(bundled, "");
        try
        {
            Assert.Equal(bundled, Resolve("models/relative-probe.onnx"));
        }
        finally
        {
            File.Delete(bundled);
            if (created) Directory.Delete(appModels, recursive: true);
        }
    }
}
