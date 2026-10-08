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
    private static int _telemetryDisabled;

    /// <summary>ONNX Runtime's Windows builds can send usage events through Windows' diagnostic data
    /// channel; LarisVMS makes no such calls (see the README's Privacy section), so turn them off.
    /// Done here — after <see cref="VisionBackendResolver"/> has pointed the loader at the chosen
    /// backend — rather than at process start, because touching <see cref="OrtEnv"/> loads the native
    /// library.</summary>
    private static void DisableTelemetryOnce(ILogger logger)
    {
        if (Interlocked.Exchange(ref _telemetryDisabled, 1) == 1) return;
        try { OrtEnv.Instance().DisableTelemetryEvents(); }
        catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException)
        {
            logger.LogDebug(ex, "Could not turn off ONNX Runtime telemetry events.");
        }
    }

    public static SessionOptions Create(EngineOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var backend = VisionBackendResolver.Backend;
        DisableTelemetryOnce(logger);

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

    /// <summary>
    /// Builds the <see cref="EngineOptions.TensorRtCacheKey"/> for one engine's exact graph variant.
    /// Everything that changes the compiled TensorRT engine goes in the name: which model file, the
    /// network size it decodes at, whether a preprocessing head was merged in (and, for slicing, for
    /// what capture size and how many slices), and the batch size. Two engines that agree on all of
    /// these are genuinely interchangeable; two that differ on any of them are not, and 0.186.0
    /// shipped with the second pair silently sharing one cached engine.
    ///
    /// Shared by both engines rather than written twice: the whole point is that the two never
    /// disagree about what makes a variant distinct, and a copy that drifts reintroduces exactly the
    /// bug this exists to prevent.
    /// </summary>
    /// <remarks>Public rather than internal because CameraDetectionPipeline (a different assembly)
    /// needs the same key to fill in <see cref="EngineOptions.TensorRtCacheKey"/> for
    /// <see cref="EngineBuildGate"/>'s warm/cold probe, before any engine exists.</remarks>
    public static string TensorRtCacheKeyFor(string modelPath, InferenceProfile profile,
        SliceLayout? sliceLayout, int batchSize, bool gpuPreprocessing)
    {
        var model = Path.GetFileNameWithoutExtension(modelPath);
        var variant = sliceLayout is { } layout
            ? $"cap{layout.CaptureWidth}x{layout.CaptureHeight}-s{layout.Slices.Count}"
            : gpuPreprocessing ? "nv12" : "raw";

        return $"{model}-net{profile.NetworkWidth}x{profile.NetworkHeight}-{variant}-b{batchSize}";
    }

    /// <summary>Reduces <see cref="EngineOptions.TensorRtCacheKey"/> to characters that are safe in a
    /// filename on every platform this can run on, since TensorRT concatenates it straight into a
    /// path. Returns null for a null/blank/entirely-unusable key, meaning "let ONNX Runtime name its
    /// own cache files". Internal so the engines' own key construction can be unit-tested against
    /// exactly the transform that will be applied to it.</summary>
    internal static string? SanitizeCacheKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        var chars = key.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray();
        var cleaned = new string(chars).Trim('-');
        return cleaned.Length == 0 ? null : cleaned;
    }

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

                // D-FINE (a DETR/transformer) overflows FP16 in its LayerNorm subgraphs under the
                // TensorRT builder — trt_layer_norm_fp32_fallback forces those Pow + Reduce ops back
                // to FP32 while the rest of the graph keeps FP16 throughput. Set only for D-FINE +
                // FP16 (see CameraDetectionPipeline); the FP16 mixed-precision model file is the
                // primary mitigation, this is a second line of defence. An ORT build that doesn't
                // know the key is handled by the retry below.
                if (options.TensorRtLayerNormFp32Fallback)
                    providerOptions["trt_layer_norm_fp32_fallback"] = "1";

                // Pin the builder to exactly one shape when this pipeline batches — see
                // EngineOptions.BatchSize/TensorRtBatchProfile's own doc comments for why the shape
                // is fixed for the pipeline's whole lifetime (never a per-call variable), so min/opt/
                // max are all identical rather than a real range. Left alone (ORT's own default
                // dynamic-shape handling) at BatchSize 1, which needs no profile at all.
                //
                // NOT verified against a real TensorRT 10.x build (this development box has no GPU) —
                // trt_profile_*_shapes' exact option-name spelling and value grammar come from ORT's
                // own documentation, not a confirmed run. If a node logs these as unrecognized/rejected,
                // that is the first thing to check on real hardware.
                if (options.BatchSize > 1)
                {
                    if (options.TensorRtBatchProfile is { } batchProfile)
                    {
                        var shape = $"{batchProfile.InputName}:{options.BatchSize}x3x{batchProfile.Height}x{batchProfile.Width}";
                        providerOptions["trt_profile_min_shapes"] = shape;
                        providerOptions["trt_profile_opt_shapes"] = shape;
                        providerOptions["trt_profile_max_shapes"] = shape;
                    }
                    else
                    {
                        logger.LogWarning(
                            "EngineOptions.BatchSize is {BatchSize} but no TensorRtBatchProfile was supplied -- " +
                            "TensorRT has no pinned shape profile and may rebuild its engine on the first real " +
                            "batch, or reject the graph outright depending on the ORT build.", options.BatchSize);
                    }
                }

                // One cache directory holds every variant this node ever builds, and ONNX Runtime's
                // own engine cache key is a hash of *names* only -- no shapes, and no model file name
                // at all for the in-memory graphs the preprocessing/slicing heads produce. Naming the
                // prefix ourselves keeps the directory readable (yolox_m-net640x640-cap1138x640-s2_...)
                // and lets EngineBuildGate probe for this variant specifically rather than for any
                // .engine file. See OnnxPreprocessHead.RetagGeneratedNames for the collision this is
                // backing up.
                var cacheKey = SanitizeCacheKey(options.TensorRtCacheKey);
                if (cacheKey is not null) providerOptions["trt_engine_cache_prefix"] = cacheKey;

                using (var trtOptions = new OrtTensorRTProviderOptions())
                {
                    // UpdateOptions rejects the whole dictionary if it doesn't recognize one key, and
                    // the outer catch would then drop this node off TensorRT entirely -- a far worse
                    // regression than losing any single one of these, none of which correctness
                    // depends on (each graph variant is already uniquely named at the graph level;
                    // the FP16 mixed-precision model is the real overflow fix). So on a rejection,
                    // drop one optional key and retry rather than losing the provider.
                    string[] optionalKeys = ["trt_engine_cache_prefix", "trt_layer_norm_fp32_fallback"];
                    while (true)
                    {
                        try
                        {
                            trtOptions.UpdateOptions(providerOptions);
                            break;
                        }
                        catch (Exception ex)
                        {
                            var drop = optionalKeys.FirstOrDefault(providerOptions.ContainsKey);
                            if (drop is null) throw; // nothing optional left -- a real failure
                            logger.LogWarning(ex,
                                "This ONNX Runtime build rejected a TensorRT provider option -- retrying without " +
                                "'{Key}'. It is an optimization, not a correctness setting: without " +
                                "trt_engine_cache_prefix the cache directory is unreadable and every start probes " +
                                "cold for this camera; without trt_layer_norm_fp32_fallback D-FINE FP16 relies solely " +
                                "on the mixed-precision model file.", drop);
                            providerOptions.Remove(drop);
                            if (drop == "trt_engine_cache_prefix") cacheKey = null;
                        }
                    }
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
                    "TensorRT execution provider appended: gpuId={GpuId}, fp16={Fp16}, layerNormFp32Fallback={LnFallback}, " +
                    "engine+timing cache={CachePath}, engine cache prefix={CachePrefix}, max builder workspace={WorkspaceMb} MB, " +
                    "builder optimization level={Level}, batchSize={BatchSize}.",
                    options.GpuId, fp16 == "1", providerOptions.ContainsKey("trt_layer_norm_fp32_fallback"),
                    cachePath, cacheKey ?? "(ORT default)",
                    options.TensorRtMaxWorkspaceBytes / (1024 * 1024),
                    options.TensorRtBuilderOptimizationLevel?.ToString() ?? "default", options.BatchSize);
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
