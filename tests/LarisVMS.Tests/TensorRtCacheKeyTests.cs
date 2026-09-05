using LarisVMS.Core.Enums;
using LarisVMS.Vision.Inference;

namespace LarisVMS.Tests;

/// <summary>
/// The TensorRT engine cache key must separate every graph variant a node can build, because ONNX
/// Runtime's own cache key does not: it hashes the model file name plus the *names* of the graph's
/// inputs and node outputs, never their shapes, and a graph merged in memory has no file name to
/// contribute at all. In 0.186.0 that let two Slice cameras of different resolutions share one
/// compiled engine, and every camera after the first failed on every frame binding its own frames.
///
/// These tests are about *separation* rather than exact spelling — the key is a filename component,
/// not a contract with anything — but a test naming the format at least makes an accidental change
/// to it deliberate.
/// </summary>
public class TensorRtCacheKeyTests
{
    private static InferenceProfile Square(int size) =>
        InferenceProfile.Create(size, size, AspectMode.Stretch, size);

    [Fact]
    public void TwoSliceCamerasOfDifferentResolutions_GetDifferentKeys()
    {
        // The exact pair from the incident: same model, same network size, same slice count, but
        // different capture buffers — everything ONNX Runtime's own key is blind to.
        var a = SliceLayout.Create(1920, 1080, 640);
        var b = SliceLayout.Create(1280, 960, 640);
        Assert.Equal(a.Slices.Count, b.Slices.Count);
        Assert.NotEqual(a.CaptureWidth, b.CaptureWidth);

        var keyA = OrtSessionFactory.TensorRtCacheKeyFor(@"C:\models\yolox_m.onnx", Square(640), a, 1, true);
        var keyB = OrtSessionFactory.TensorRtCacheKeyFor(@"C:\models\yolox_m.onnx", Square(640), b, 1, true);

        Assert.NotEqual(keyA, keyB);
        Assert.Equal("yolox_m-net640x640-cap1138x640-s2-b1", keyA);
    }

    [Fact]
    public void EveryVariantAxis_ChangesTheKey()
    {
        var layout = SliceLayout.Create(1920, 1080, 640);
        var baseline = OrtSessionFactory.TensorRtCacheKeyFor(@"C:\models\yolox_m.onnx", Square(640), layout, 1, true);

        var keys = new[]
        {
            baseline,
            // A different model file.
            OrtSessionFactory.TensorRtCacheKeyFor(@"C:\models\yolox_s.onnx", Square(640), layout, 1, true),
            // A different network size (the 416 YOLOX sizes).
            OrtSessionFactory.TensorRtCacheKeyFor(@"C:\models\yolox_m.onnx", Square(416), SliceLayout.Create(1920, 1080, 416), 1, true),
            // Not sliced, but still carrying a merged nv12 preprocessing head.
            OrtSessionFactory.TensorRtCacheKeyFor(@"C:\models\yolox_m.onnx", Square(640), null, 1, true),
            // Not sliced and no head at all — the plain path.
            OrtSessionFactory.TensorRtCacheKeyFor(@"C:\models\yolox_m.onnx", Square(640), null, 1, false),
            // A different batch size.
            OrtSessionFactory.TensorRtCacheKeyFor(@"C:\models\yolox_m.onnx", Square(640), null, 2, false),
        };

        Assert.Equal(keys.Length, keys.Distinct().Count());
    }

    [Fact]
    public void KeysAreSafeToUseAsAFilenameComponent()
    {
        // TensorRT concatenates the prefix straight into a path, so anything that isn't a plain
        // filename character has to be replaced rather than passed through.
        Assert.Equal("a-b-c.d_e", OrtSessionFactory.SanitizeCacheKey("a/b\\c.d_e"));
        Assert.Equal("model-net640x640-raw-b1",
            OrtSessionFactory.SanitizeCacheKey(
                OrtSessionFactory.TensorRtCacheKeyFor(@"C:\models\model.onnx", Square(640), null, 1, false)));

        // Null means "let ONNX Runtime name its own cache files", which is what every build before
        // 0.186.2 did — EngineBuildGate's warm probe falls back to its old behavior on it.
        Assert.Null(OrtSessionFactory.SanitizeCacheKey(null));
        Assert.Null(OrtSessionFactory.SanitizeCacheKey("   "));
        Assert.Null(OrtSessionFactory.SanitizeCacheKey("///"));
    }
}
