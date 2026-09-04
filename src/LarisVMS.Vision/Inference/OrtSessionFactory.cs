using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace LarisVMS.Vision.Inference;

/// <summary>
/// Builds a <see cref="SessionOptions"/> configured for whichever ONNX Runtime backend
/// <see cref="VisionBackendResolver"/> selected for this process at startup (CUDA, DirectML or CPU;
/// OpenVINO only via an explicit override and a hand-placed backend folder) -- the successor to the
/// old compile-time <c>#if ACCEL_*</c> switch. The node package ships the CUDA and DirectML native
/// builds; the resolver points the OS loader at one folder and this method appends the matching
/// execution provider, degrading to the CPU provider inside the same native build if the GPU
/// provider fails to initialize (and recording that on <see cref="VisionBackendResolver.Current"/>
/// so the control plane can flag the node).
///
/// <see cref="EngineOptions"/> still carries the machine-level knobs (GPU id, cuDNN/TensorRT paths,
/// OpenVINO device); only the ones relevant to the resolved backend do anything.
/// </summary>
public static class OrtSessionFactory
{
    public static SessionOptions Create(EngineOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var backend = VisionBackendResolver.Backend;

        try
        {
            return backend switch
            {
                VisionBackend.Cuda => CreateCuda(options, logger),
                VisionBackend.DirectMl => CreateDirectMl(options, logger),
                VisionBackend.OpenVino => CreateSimple("OpenVINO", o => o.AppendExecutionProvider_OpenVINO(options.OpenVinoDeviceType), logger),
                _ => CreateCpu(logger),
            };
        }
        catch (Exception ex)
        {
            // The GPU provider failed to bind (a missing native dependency the cheap startup probe
            // didn't catch, a driver the OS wouldn't hand out). Fall back to CPU within the loaded
            // build so detection still runs, and record it so Admin/Nodes can show the node needs
            // attention.
            logger.LogError(ex,
                "The {Backend} execution provider failed to initialize -- this process will run detection on CPU.",
                backend);
            VisionBackendResolver.MarkDegradedToCpu(
                $"the {backend} execution provider failed to initialize on this node ({ex.Message})");
            return CreateCpu(logger);
        }
    }

    private static SessionOptions CreateCpu(ILogger logger)
    {
        var sessionOptions = new SessionOptions();
        sessionOptions.AppendExecutionProvider_CPU(1);
        logger.LogInformation("CPU execution provider.");
        return sessionOptions;
    }

    private static SessionOptions CreateSimple(string provider, Action<SessionOptions> append, ILogger logger)
    {
        var sessionOptions = new SessionOptions();
        append(sessionOptions);
        VisionBackendResolver.SetEffectiveProvider(provider);
        logger.LogInformation("{Provider} execution provider appended.", provider);
        return sessionOptions;
    }

    private static SessionOptions CreateDirectMl(EngineOptions options, ILogger logger)
    {
        // The DirectML execution provider requires these two — it does not support ORT's memory
        // pattern optimizer and only runs in sequential execution mode (ONNX Runtime's own documented
        // constraint). Setting them after the fact throws.
        var sessionOptions = new SessionOptions
        {
            EnableMemoryPattern = false,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };
        sessionOptions.AppendExecutionProvider_DML(options.GpuId);
        VisionBackendResolver.SetEffectiveProvider("DirectML");
        logger.LogInformation("DirectML execution provider: deviceId={DeviceId}.", options.GpuId);
        return sessionOptions;
    }

    /// <summary>
    /// Where TensorRT keeps its compiled engines and timing cache. A configured path wins; otherwise
    /// this defaults rather than throwing, which is what it used to do. There is no sensible "no
    /// cache" mode -- without one every process start recompiles from scratch -- so making the
    /// setting mandatory only meant an operator who set <c>Vision__EnableTensorRt</c> alone got a
    /// silent fall-through to plain CUDA. Same %ProgramData%\LarisVMS root the vision log already
    /// writes to, so it exists and is writable wherever this service can run at all.
    /// </summary>
    internal static string ResolveTensorRtCachePath(string? configured) =>
        !string.IsNullOrWhiteSpace(configured)
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "LarisVMS", "trt-cache");

    private static SessionOptions CreateCuda(EngineOptions options, ILogger logger)
    {
        var sessionOptions = new SessionOptions();

        EngineFactory.PrependToPath(options.CudnnPath, "cudnn64_9.dll", "cuDNN", logger);

        var tensorRtAppended = false;
        if (options.EnableTensorRt)
        {
            // TensorRT sits on top of CUDA, and its native library is a separate SDK download whose
            // version must match ONNX Runtime's. If any of that isn't right, fall through to plain
            // CUDA rather than losing the GPU entirely -- a TensorRT failure is not a reason to run on
            // CPU.
            try
            {
                EngineFactory.PrependToPath(options.TensorRtLibPath, "nvinfer_10.dll", "TensorRT", logger);

                var cachePath = ResolveTensorRtCachePath(options.TensorRtEngineCachePath);

                // ONNX Runtime 1.23's C# API no longer accepts the classic TensorRT EP via the
                // string-keyed AppendExecutionProvider("Tensorrt", ...) overload. Configure it
                // through OrtTensorRTProviderOptions instead (the ORT_TENSORRT_* environment
                // variables are NOT honored by this build) -- notably the engine + timing caches, so
                // the multi-minute engine build only happens once rather than on every restart.
                // Requires TensorRT 10.x (this ORT build links nvinfer_10.dll); a TensorRT 11 install
                // (nvinfer_11.dll) fails the load and drops through to plain CUDA below.
                var fp16 = options.TensorRtPrecision.Equals("FP16", StringComparison.OrdinalIgnoreCase) ? "1" : "0";
                Directory.CreateDirectory(cachePath);

                var providerOptions = new Dictionary<string, string>
                {
                    ["device_id"] = options.GpuId.ToString(),
                    ["trt_fp16_enable"] = fp16,
                    ["trt_engine_cache_enable"] = "1",
                    ["trt_engine_cache_path"] = cachePath,
                    ["trt_timing_cache_enable"] = "1",
                    ["trt_timing_cache_path"] = cachePath,
                    // Left unset, TensorRT 10's builder may claim the entire device while compiling --
                    // on a node concurrently running NVDEC decode for capture, motion and high-res
                    // re-detection, that is a real contention source rather than a theoretical one.
                    ["trt_max_workspace_size"] = options.TensorRtMaxWorkspaceBytes.ToString(),
                };
                if (options.TensorRtBuilderOptimizationLevel is { } level)
                    providerOptions["trt_builder_optimization_level"] = level.ToString();

                using (var trtOptions = new OrtTensorRTProviderOptions())
                {
                    trtOptions.UpdateOptions(providerOptions);
                    // The append copies the options into the session; disposing trtOptions after is safe.
                    sessionOptions.AppendExecutionProvider_Tensorrt(trtOptions);
                }
                tensorRtAppended = true;
                VisionBackendResolver.SetEffectiveProvider("TensorRT");

                // Note this logs that the provider was *appended*, not that an engine exists yet --
                // the compile happens later, inside InferenceSession's constructor. EngineBuildGate
                // logs that half, including whether the cache was cold; reading only this line during
                // a cold start is what made a multi-minute stall look like a healthy startup.
                logger.LogInformation(
                    "TensorRT execution provider appended: gpuId={GpuId}, fp16={Fp16}, engine+timing cache={CachePath}, " +
                    "max builder workspace={WorkspaceMb} MB, builder optimization level={Level}.",
                    options.GpuId, fp16 == "1", cachePath,
                    options.TensorRtMaxWorkspaceBytes / (1024 * 1024),
                    options.TensorRtBuilderOptimizationLevel?.ToString() ?? "default");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "TensorRT was requested but could not be enabled -- continuing on plain CUDA. Needs TensorRT 10.x " +
                    "(nvinfer_10.dll) matching this ONNX Runtime build, with Vision:TensorRtLibPath pointing at its folder.");
                VisionBackendResolver.NoteTensorRtUnavailable(ex.Message);
            }
        }

        // Appended after TensorRT (when it took) as the fallback for any op TensorRT doesn't support
        // -- ONNX Runtime tries providers in append order, first match wins per op.
        sessionOptions.AppendExecutionProvider_CUDA(options.GpuId);
        if (!tensorRtAppended) VisionBackendResolver.SetEffectiveProvider("CUDA");
        logger.LogInformation("CUDA execution provider: gpuId={GpuId}.", options.GpuId);

        return sessionOptions;
    }
}
