using LarisVMS.Vision.Service;
using Microsoft.Extensions.Logging.Abstractions;

namespace LarisVMS.Tests;

/// <summary>
/// Covers CameraPipelineManager.ResolveModelPath — the fallback that lets a node find whatever .onnx
/// was actually bundled instead of requiring one specific filename. The shipped default
/// (models/model.onnx) named a file nothing in this project ever produces, so every node failed to
/// start detection with "File doesn't exist" no matter how correctly it was otherwise set up; these
/// pin the discovery behavior that replaced it.
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

    private static string Resolve(string configuredPath) =>
        CameraPipelineManager.ResolveModelPath(configuredPath, NullLogger.Instance);

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
        // A second file that would win the alphabetical fallback, proving the explicit path is not
        // merely being rediscovered by chance.
        File.WriteAllText(Path.Combine(dir, "aaa.onnx"), "");

        Assert.Equal(configured, Resolve(configured));
    }

    [Fact]
    public void FallsBackToTheOnlyBundledModelWhenTheConfiguredNameDoesNotExist()
    {
        // The exact production failure: the default names model.onnx, the package contains the
        // exporter's own filename instead.
        var dir = NewModelsDirectory();
        var bundled = Path.Combine(dir, "yolo9-t.onnx");
        File.WriteAllText(bundled, "");

        Assert.Equal(bundled, Resolve(Path.Combine(dir, "model.onnx")));
    }

    [Fact]
    public void PicksDeterministicallyByNameWhenSeveralModelsAreBundled()
    {
        // export.py's default set is two models, so this is the ordinary case, not an error.
        var dir = NewModelsDirectory();
        foreach (var name in new[] { "yolo9-t.onnx", "yolo9-s.onnx", "yolo9-m.onnx" })
            File.WriteAllText(Path.Combine(dir, name), "");

        var first = Resolve(Path.Combine(dir, "model.onnx"));
        var again = Resolve(Path.Combine(dir, "model.onnx"));

        Assert.Equal(Path.Combine(dir, "yolo9-m.onnx"), first);
        Assert.Equal(first, again); // stable across calls — a node must not switch models on restart
    }

    [Fact]
    public void IgnoresNonOnnxFilesInTheModelsDirectory()
    {
        // build-node.ps1 also drops a .LICENSE.txt beside each exported model.
        var dir = NewModelsDirectory();
        File.WriteAllText(Path.Combine(dir, "yolo9-t.onnx.LICENSE.txt"), "");
        File.WriteAllText(Path.Combine(dir, "readme.md"), "");
        var bundled = Path.Combine(dir, "yolo9-t.onnx");
        File.WriteAllText(bundled, "");

        Assert.Equal(bundled, Resolve(Path.Combine(dir, "model.onnx")));
    }

    [Fact]
    public void ThrowsNamingTheDirectoryWhenNoModelIsBundled()
    {
        var dir = NewModelsDirectory();

        var ex = Assert.Throws<FileNotFoundException>(() => Resolve(Path.Combine(dir, "model.onnx")));
        Assert.Contains(dir, ex.Message);
        Assert.Contains("export-models", ex.Message);
    }

    [Fact]
    public void ThrowsWhenTheModelsDirectoryDoesNotExistAtAll()
    {
        var missing = Path.Combine(_root, "nonexistent", "model.onnx");

        var ex = Assert.Throws<FileNotFoundException>(() => Resolve(missing));
        Assert.Contains("export-models", ex.Message);
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
