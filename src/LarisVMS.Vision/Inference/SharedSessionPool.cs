using Microsoft.ML.OnnxRuntime;

namespace LarisVMS.Vision.Inference;

/// <summary>
/// Process-wide pool of ONNX Runtime sessions, shared between cameras running the same model variant.
///
/// Every camera used to build its own <see cref="InferenceSession"/>, so six cameras on one model held
/// six copies of its weights and runtime state. That native memory (not the .NET heap) was most of the
/// Vision Service's footprint on a memory-tight recorder. Engines stay per camera — each keeps its own
/// input buffers, profile, timings and latches, none of which are safe to share across threads — and
/// only lease the session itself from here.
///
/// A session is shared by at most <see cref="EngineOptions.MaxCamerasPerSession"/> engines, not all of
/// them: ONNX Runtime's TensorRT provider runs one inference at a time per session, so a single session
/// for every camera would queue them all behind each other. The cap trades some memory back for
/// parallelism. A lease is released on engine disposal; the session goes when its last lease does.
/// </summary>
public static class SharedSessionPool
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, List<Entry>> Entries = new(StringComparer.Ordinal);

    /// <summary>Leases a session for <paramref name="key"/>: an existing one with a free slot under
    /// <paramref name="maxLeases"/>, otherwise a new one from <paramref name="create"/>. A null key is
    /// never shared.</summary>
    public static SessionLease Acquire(string? key, int maxLeases, Func<InferenceSession> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        maxLeases = Math.Max(1, maxLeases);
        var serializeRuns = VisionBackendResolver.Backend == VisionBackend.DirectMl;

        if (key is not null)
        {
            lock (Gate)
            {
                if (Entries.TryGetValue(key, out var list))
                {
                    foreach (var existing in list)
                    {
                        if (existing.Leases >= maxLeases) continue;
                        existing.Leases++;
                        return new SessionLease(existing, existing.Leases);
                    }
                }
            }
        }

        // Built outside the lock: a cold TensorRT build takes minutes. EngineBuildGate already
        // serializes builds, so two cameras racing to create the same variant is not the normal case;
        // if it happens, both sessions are simply kept and each counts toward its own cap.
        var entry = new Entry(key, create(), serializeRuns ? new Lock() : null) { Leases = 1 };
        if (key is not null)
        {
            lock (Gate)
            {
                if (!Entries.TryGetValue(key, out var list)) Entries[key] = list = [];
                list.Add(entry);
            }
        }
        return new SessionLease(entry, 1);
    }

    /// <summary>The pool key for one session variant: the engine's TensorRT cache key (model name, net
    /// size, merged-head variant, batch) plus everything that key leaves out but the session bakes in.
    /// Null — not shared — when the engine supplied no cache key.</summary>
    public static string? KeyFor(EngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.TensorRtCacheKey is not { } variant) return null;
        return string.Join('|',
            Path.GetFullPath(options.ModelPath), variant, $"gpu{options.GpuId}",
            options.EnableTensorRt ? $"trt-{options.TensorRtPrecision}{(options.TensorRtLayerNormFp32Fallback ? "-ln32" : "")}" : "notrt",
            options.OpenVinoDeviceType);
    }

    internal static void Release(Entry entry)
    {
        lock (Gate)
        {
            if (--entry.Leases > 0) return;
            if (entry.Key is not null && Entries.TryGetValue(entry.Key, out var list))
            {
                list.Remove(entry);
                if (list.Count == 0) Entries.Remove(entry.Key);
            }
        }
        entry.Session.Dispose();
    }

    internal sealed class Entry(string? key, InferenceSession session, Lock? runLock)
    {
        public string? Key { get; } = key;
        public InferenceSession Session { get; } = session;
        public Lock? RunLock { get; } = runLock;
        public int Leases { get; set; }
    }
}

/// <summary>One engine's hold on a pooled <see cref="InferenceSession"/>. Dispose instead of disposing
/// the session.</summary>
public sealed class SessionLease : IDisposable
{
    private readonly SharedSessionPool.Entry _entry;
    private int _disposed;

    internal SessionLease(SharedSessionPool.Entry entry, int leaseNumber)
    {
        _entry = entry;
        LeaseNumber = leaseNumber;
    }

    public InferenceSession Session => _entry.Session;

    /// <summary>1 for the engine that created the session, 2+ for engines that joined it.</summary>
    public int LeaseNumber { get; }

    public bool IsShared => LeaseNumber > 1;

    /// <summary><see cref="InferenceSession.Run(IReadOnlyCollection{NamedOnnxValue})"/>, one call at a
    /// time on DirectML, which does not allow concurrent runs on one session; concurrent elsewhere.</summary>
    public IDisposableReadOnlyCollection<DisposableNamedOnnxValue> Run(IReadOnlyCollection<NamedOnnxValue> inputs)
    {
        if (_entry.RunLock is not { } runLock) return _entry.Session.Run(inputs);
        lock (runLock) return _entry.Session.Run(inputs);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) SharedSessionPool.Release(_entry);
    }
}
