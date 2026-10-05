using LarisVMS.Vision.Inference;
using Microsoft.ML.OnnxRuntime;

namespace LarisVMS.Tests;

/// <summary>
/// SharedSessionPool — cameras on the same model variant share one ONNX Runtime session, up to a cap
/// per session. Runs on the CPU execution provider with a tiny identity model.
/// </summary>
public class SharedSessionPoolTests
{
    private static readonly string ModelPath =
        GenericOnnxEngineSlicedVerificationTests.WriteTempModel(GenericOnnxEngineSlicedVerificationTests.BuildStaticBatchOneIdentityModel(8, 8));

    private static string UniqueKey() => "test-" + Guid.NewGuid().ToString("N");

    private static Func<InferenceSession> Create(Action? onCreate = null) => () =>
    {
        onCreate?.Invoke();
        return new InferenceSession(ModelPath);
    };

    [Fact]
    public void SameKeySharesOneSessionUpToTheCap()
    {
        var key = UniqueKey();
        var created = 0;

        using var a = SharedSessionPool.Acquire(key, 2, Create(() => created++));
        using var b = SharedSessionPool.Acquire(key, 2, Create(() => created++));
        using var c = SharedSessionPool.Acquire(key, 2, Create(() => created++));

        Assert.Same(a.Session, b.Session);
        Assert.False(a.IsShared);
        Assert.True(b.IsShared);
        Assert.NotSame(a.Session, c.Session);
        Assert.Equal(2, created);
    }

    [Fact]
    public void AFreedSlotIsReused()
    {
        var key = UniqueKey();
        using var a = SharedSessionPool.Acquire(key, 2, Create());
        var b = SharedSessionPool.Acquire(key, 2, Create());
        b.Dispose();

        using var c = SharedSessionPool.Acquire(key, 2, Create());

        Assert.Same(a.Session, c.Session);
    }

    [Fact]
    public void ReleasingTheLastLeaseDisposesTheSession()
    {
        var key = UniqueKey();
        var a = SharedSessionPool.Acquire(key, 2, Create());
        var b = SharedSessionPool.Acquire(key, 2, Create());
        var session = a.Session;

        a.Dispose();
        a.Dispose(); // a second dispose must not release b's slot
        Assert.Equal("pixel_values", session.InputMetadata.Keys.First());

        b.Dispose();

        // Nothing left under the key: the next acquire creates a fresh session.
        var created = false;
        using var c = SharedSessionPool.Acquire(key, 2, Create(() => created = true));
        Assert.True(created);
    }

    [Fact]
    public void NullKeyIsNeverShared()
    {
        using var a = SharedSessionPool.Acquire(null, 2, Create());
        using var b = SharedSessionPool.Acquire(null, 2, Create());

        Assert.NotSame(a.Session, b.Session);
    }

    [Fact]
    public void KeySeparatesEverythingTheSessionBakesIn()
    {
        var baseOptions = new EngineOptions { ModelPath = ModelPath, TensorRtCacheKey = "m-net640x640-raw-b1", EnableTensorRt = true };

        var key = SharedSessionPool.KeyFor(baseOptions);
        Assert.Equal(key, SharedSessionPool.KeyFor(baseOptions with { }));
        Assert.NotEqual(key, SharedSessionPool.KeyFor(baseOptions with { TensorRtPrecision = "FP32" }));
        Assert.NotEqual(key, SharedSessionPool.KeyFor(baseOptions with { TensorRtLayerNormFp32Fallback = true }));
        Assert.NotEqual(key, SharedSessionPool.KeyFor(baseOptions with { GpuId = 1 }));
        Assert.NotEqual(key, SharedSessionPool.KeyFor(baseOptions with { TensorRtCacheKey = "m-net640x640-cap1136x640-s2-b1" }));
        Assert.Null(SharedSessionPool.KeyFor(baseOptions with { TensorRtCacheKey = null }));
    }

    [Fact]
    public void LeasedSessionRuns()
    {
        using var lease = SharedSessionPool.Acquire(UniqueKey(), 2, Create());
        var input = new Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float>([1, 3, 8, 8]);
        input[0, 0, 0, 0] = 42;

        using var outputs = lease.Run([NamedOnnxValue.CreateFromTensor("pixel_values", input)]);

        Assert.Equal(42, outputs.First().AsTensor<float>()[0, 0, 0, 0]);
    }
}
