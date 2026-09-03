using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;

namespace LarisVMS.Vision.Inference;

/// <summary>Operator-facing summary of which ONNX Runtime backend this process actually ended up
/// running against, and anything a site admin should do about it (install a toolkit, update a
/// driver). Serialized back to the control plane on the node heartbeat and shown on Admin/Nodes.</summary>
public sealed record VisionBackendReport
{
    /// <summary>The <c>AiAccelerator</c> the node asked this process to prefer — "Nvidia", "Intel",
    /// "Amd", "Cpu", or "Auto" when nothing was passed.</summary>
    public required string RequestedAccelerator { get; init; }

    /// <summary>The native ONNX Runtime build actually loaded.</summary>
    public required VisionBackend ResolvedBackend { get; init; }

    /// <summary>The execution provider inference actually runs on — "CUDA", "TensorRT", "DirectML",
    /// "OpenVINO" or "CPU". Differs from <see cref="ResolvedBackend"/> when a provider failed to
    /// initialize and the session fell back to CPU within the same native build.</summary>
    public required string EffectiveExecutionProvider { get; init; }

    /// <summary>True when the preferred GPU provider could not be used and inference is on CPU.</summary>
    public bool DegradedToCpu { get; init; }

    /// <summary>Human-readable notes for a site admin, e.g. "NVIDIA GPU detected but the CUDA Toolkit
    /// was not found — running DirectML. Install CUDA Toolkit 12.x + cuDNN 9.x on this node for CUDA
    /// acceleration." Empty when nothing needs attention.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public bool NeedsAttention => DegradedToCpu || Notes.Count > 0;
}

/// <summary>
/// Chooses which of the bundled native ONNX Runtime builds this process loads, once, at startup —
/// the runtime replacement for the old compile-time <c>-p:Accel=</c> switch. Every node package now
/// ships the <c>cuda</c> and <c>directml</c> native builds under <c>onnxruntime-backends/</c> (CPU
/// is the plain <c>onnxruntime.dll</c> at the app root); this points the OS loader (a <see
/// cref="NativeLibrary.SetDllImportResolver"/> hook plus a PATH prepend) at one of them based on the
/// accelerator the node resolved for its hardware, degrading down the list
/// — CUDA → DirectML → CPU — when a backend's external prerequisites (CUDA Toolkit, cuDNN) aren't
/// installed. <see cref="OrtSessionFactory"/> then appends the matching execution provider and, if
/// even that fails, falls back to the CPU provider within the loaded build and records it here.
/// </summary>
public static class VisionBackendResolver
{
    private static readonly Lock Gate = new();
    private static bool _resolved;

    private static string _requestedAccelerator = "Auto";
    private static VisionBackend _backend = VisionBackend.Cpu;
    private static string _effectiveProvider = "CPU";
    private static bool _degraded;
    private static readonly List<string> _notes = [];

    /// <summary>Test-only override of <see cref="BackendsRoot"/>.</summary>
    internal static string? BackendsRootOverride;

    /// <summary>Folder the backend subdirectories live in — beside the executable, laid out by
    /// LarisVMS.Vision.Service's <c>StageOnnxBackends</c> MSBuild target.</summary>
    public static string BackendsRoot =>
        BackendsRootOverride ?? Path.Combine(AppContext.BaseDirectory, "onnxruntime-backends");

    /// <summary>Resolved backend, forcing resolution from environment variables if it hasn't happened
    /// yet (the path a unit test or a bare `dotnet run` takes; <see cref="Initialize"/> is the normal
    /// one).</summary>
    public static VisionBackend Backend
    {
        get
        {
            EnsureResolved(_ => null, NullLogger.Instance);
            return _backend;
        }
    }

    public static VisionBackendReport Current
    {
        get
        {
            lock (Gate)
            {
                return new VisionBackendReport
                {
                    RequestedAccelerator = _requestedAccelerator,
                    ResolvedBackend = _backend,
                    EffectiveExecutionProvider = _effectiveProvider,
                    DegradedToCpu = _degraded,
                    Notes = _notes.ToArray(),
                };
            }
        }
    }

    /// <summary>Called once from Program.cs with a lookup over the process configuration (so the
    /// Vision library needn't reference <c>Microsoft.Extensions.Configuration</c>). Idempotent.</summary>
    public static void Initialize(Func<string, string?> configLookup, ILogger logger)
        => EnsureResolved(configLookup, logger);

    private static void EnsureResolved(Func<string, string?> lookup, ILogger logger)
    {
        lock (Gate)
        {
            if (_resolved) return;
            var preferred = lookup("Vision:PreferredAccelerator")
                ?? Environment.GetEnvironmentVariable("Vision__PreferredAccelerator");
            var backendOverride = lookup("Vision:Backend")
                ?? Environment.GetEnvironmentVariable("Vision__Backend");
            // So the CUDA prerequisite probe below sees a cuDNN / TensorRT install that's only
            // reachable via Vision:CudnnPath (e.g. pip's site-packages), not the system PATH.
            string?[] extraNativeDirs =
            [
                lookup("Vision:CudnnPath") ?? Environment.GetEnvironmentVariable("Vision__CudnnPath"),
                lookup("Vision:TensorRtLibPath") ?? Environment.GetEnvironmentVariable("Vision__TensorRtLibPath"),
            ];
            Resolve(preferred, backendOverride, logger, extraNativeDirs);
            _resolved = true;
        }
    }

    /// <summary>Testable core. <paramref name="preferredAccelerator"/> is the node's resolved
    /// <c>AiAccelerator</c> name; <paramref name="backendOverride"/> forces a specific backend
    /// ("cuda"/"directml"/"openvino"/"cpu") regardless of hardware; <paramref name="extraNativeDirs"/>
    /// are prepended to PATH before the CUDA prerequisite probe.</summary>
    internal static void Resolve(string? preferredAccelerator, string? backendOverride, ILogger logger,
        params string?[] extraNativeDirs)
    {
        _notes.Clear();
        _degraded = false;
        _requestedAccelerator = string.IsNullOrWhiteSpace(preferredAccelerator) ? "Auto" : preferredAccelerator.Trim();

        foreach (var dir in extraNativeDirs)
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) PrependToPath(dir);

        VisionBackend chosen;
        if (!string.IsNullOrWhiteSpace(backendOverride)
            && Enum.TryParse<VisionBackend>(NormalizeBackendName(backendOverride), ignoreCase: true, out var forced))
        {
            chosen = HasBackendFolder(forced) ? forced : Fallback(forced, $"'{backendOverride}' backend was requested but its folder is missing from the package");
        }
        else
        {
            chosen = _requestedAccelerator.ToLowerInvariant() switch
            {
                "nvidia" => ResolveNvidia(logger),
                "intel" or "amd" => HasBackendFolder(VisionBackend.DirectMl)
                    ? VisionBackend.DirectMl
                    : Fallback(VisionBackend.DirectMl, MissingFolderReason("DirectML")),
                _ => VisionBackend.Cpu,
            };
        }

        _backend = chosen;
        _effectiveProvider = chosen switch
        {
            VisionBackend.Cuda => "CUDA",
            VisionBackend.DirectMl => "DirectML",
            VisionBackend.OpenVino => "OpenVINO",
            _ => "CPU",
        };

        if (chosen != VisionBackend.Cpu)
        {
            PointLoaderAt(BackendFolder(chosen), logger);
        }

        logger.LogInformation(
            "Vision backend resolved: requested={Requested}, backend={Backend}, provider={Provider}{Notes}.",
            _requestedAccelerator, _backend, _effectiveProvider,
            _notes.Count > 0 ? " — " + string.Join(" ", _notes) : string.Empty);
    }

    private static VisionBackend ResolveNvidia(ILogger logger)
    {
        var cudaDir = BackendFolder(VisionBackend.Cuda);

        if (!File.Exists(Path.Combine(cudaDir, "onnxruntime.dll")))
            return Fallback(VisionBackend.Cuda, MissingFolderReason("CUDA"));

        // onnxruntime_providers_cuda.dll (~320 MB) isn't in the node package — the node downloads it
        // from the server in the background once it's an NVIDIA machine with the CUDA Toolkit
        // installed. Until it lands, DirectML.
        if (!File.Exists(Path.Combine(cudaDir, "onnxruntime_providers_cuda.dll")))
            return Fallback(VisionBackend.Cuda,
                "the CUDA provider library hasn't been fetched from the server yet — the node downloads it in the background");

        // Put the CUDA folder on PATH first — install-node.ps1 copies the CUDA Toolkit / cuDNN
        // runtime DLLs there when it finds them, so the probe below should see them.
        PrependToPath(cudaDir);

        var haveCudart = NativeLibLoadable("cudart64_12");
        var haveCudnn = NativeLibLoadable("cudnn64_9");
        if (haveCudart && haveCudnn) return VisionBackend.Cuda;

        var missing = !haveCudart && !haveCudnn ? "the CUDA Toolkit 12.x runtime and cuDNN 9.x"
            : !haveCudart ? "the CUDA Toolkit 12.x runtime (cudart64_12.dll)"
            : "cuDNN 9.x (cudnn64_9.dll)";
        return Fallback(VisionBackend.Cuda,
            $"an NVIDIA GPU was selected but {missing} could not be found on this node — install it and re-run install-node.ps1 for CUDA acceleration");
    }

    /// <summary>Steps a failed backend down to the next viable one and records why.</summary>
    private static VisionBackend Fallback(VisionBackend from, string? reason)
    {
        if (reason is not null) _notes.Add(Capitalize(reason) + ".");

        foreach (var next in NextBest(from))
        {
            if (HasBackendFolder(next))
            {
                if (next != VisionBackend.Cpu)
                    _notes.Add($"Running {DisplayName(next)} instead.");
                return next;
            }
        }
        return VisionBackend.Cpu;
    }

    private static IEnumerable<VisionBackend> NextBest(VisionBackend from) => from switch
    {
        VisionBackend.Cuda => [VisionBackend.DirectMl, VisionBackend.Cpu],
        VisionBackend.OpenVino => [VisionBackend.DirectMl, VisionBackend.Cpu],
        VisionBackend.DirectMl => [VisionBackend.Cpu],
        _ => [VisionBackend.Cpu],
    };

    /// <summary>Called by <see cref="OrtSessionFactory"/> when appending the preferred execution
    /// provider throws and it falls back to the CPU provider inside the already-loaded build.</summary>
    public static void MarkDegradedToCpu(string reason)
    {
        lock (Gate)
        {
            _degraded = true;
            _effectiveProvider = "CPU";
            _notes.Add(Capitalize(reason.TrimEnd('.')) + ".");
        }
    }

    /// <summary>Called by <see cref="OrtSessionFactory"/> to record the provider it actually appended
    /// (e.g. "TensorRT" when TensorRT is enabled on top of CUDA).</summary>
    public static void SetEffectiveProvider(string provider)
    {
        lock (Gate)
        {
            if (!_degraded) _effectiveProvider = provider;
        }
    }

    /// <summary>Called by <see cref="OrtSessionFactory"/> when TensorRT was requested but failed to
    /// initialize and it fell through to plain CUDA — surfaced on Admin/Nodes so the version mismatch
    /// is visible.</summary>
    public static void NoteTensorRtUnavailable(string reason)
    {
        lock (Gate)
        {
            var note = $"TensorRT was requested but could not be enabled ({reason.TrimEnd('.')}) — running plain CUDA.";
            if (!_notes.Contains(note)) _notes.Add(note);
        }
    }

    private static string BackendFolder(VisionBackend backend) => Path.Combine(BackendsRoot, DirName(backend));

    private static bool HasBackendFolder(VisionBackend backend)
    {
        if (backend == VisionBackend.Cpu) return true;
        var dir = BackendFolder(backend);
        if (!File.Exists(Path.Combine(dir, "onnxruntime.dll"))) return false;
        // The CUDA backend also needs its big provider library, which the node fetches from the
        // server rather than shipping in the package.
        return backend != VisionBackend.Cuda || File.Exists(Path.Combine(dir, "onnxruntime_providers_cuda.dll"));
    }

    private static string DirName(VisionBackend backend) => backend switch
    {
        VisionBackend.Cuda => "cuda",
        VisionBackend.DirectMl => "directml",
        VisionBackend.OpenVino => "openvino",
        _ => "cpu",
    };

    private static string DisplayName(VisionBackend backend) => backend switch
    {
        VisionBackend.Cuda => "CUDA",
        VisionBackend.DirectMl => "DirectML",
        VisionBackend.OpenVino => "OpenVINO",
        _ => "CPU",
    };

    private static string NormalizeBackendName(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "directml" or "dml" => nameof(VisionBackend.DirectMl),
        "openvino" or "vino" => nameof(VisionBackend.OpenVino),
        "cuda" or "gpu" or "nvidia" => nameof(VisionBackend.Cuda),
        _ => nameof(VisionBackend.Cpu),
    };

    private static void PointLoaderAt(string backendFolder, ILogger logger)
    {
        PrependToPath(backendFolder);
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(OrtEnv).Assembly, (name, _, _) =>
            {
                if (!string.Equals(name, "onnxruntime", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
                var dll = Path.Combine(backendFolder, "onnxruntime.dll");
                return File.Exists(dll) && NativeLibrary.TryLoad(dll, out var handle) ? handle : IntPtr.Zero;
            });
        }
        catch (InvalidOperationException)
        {
            // A resolver is already registered for this assembly (a second Resolve in the same
            // process, only reachable from tests) — the first registration already points here.
            logger.LogDebug("An onnxruntime DllImport resolver was already registered — keeping it.");
        }
    }

    private static void PrependToPath(string directory)
    {
        var current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (!current.Split(Path.PathSeparator).Contains(directory, StringComparer.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + current);
    }

    private static bool NativeLibLoadable(string name)
    {
        try
        {
            if (NativeLibrary.TryLoad(name, out var handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }
        }
        catch
        {
            // TryLoad can still throw for a malformed name / access issue — treat as "not available".
        }
        return false;
    }

    private static string MissingFolderReason(string backend) =>
        $"the {backend} backend is missing from this install (onnxruntime-backends\\) — re-run install-node.ps1 " +
        "to get the bundled backends; auto-update only replaces the executable";

    private static string Capitalize(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
