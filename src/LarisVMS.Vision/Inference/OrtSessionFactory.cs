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

                if (string.IsNullOrWhiteSpace(options.TensorRtEngineCachePath))
                {
                    throw new InvalidOperationException(
                        "EnableTensorRt requires TensorRtEngineCachePath -- without a cache, every " +
                        "process start rebuilds the engine from scratch, which can take minutes.");
                }

                // ONNX Runtime 1.23's C# API no longer accepts the classic TensorRT EP via the
                // string-keyed AppendExecutionProvider("Tensorrt", ...) overload -- only the typed
                // AppendExecutionProvider_Tensorrt(deviceId). Its options come from ORT_TENSORRT_*
                // environment variables, read by the provider at init (set here, in-process, right
                // before the append). Requires TensorRT 10.x (this ORT build links nvinfer_10.dll) --
                // a TensorRT 11 install (nvinfer_11.dll) will fail the load and drop through to plain
                // CUDA below.
                var fp16 = options.TensorRtPrecision.Equals("FP16", StringComparison.OrdinalIgnoreCase) ? "1" : "0";
                Directory.CreateDirectory(options.TensorRtEngineCachePath);
                Environment.SetEnvironmentVariable("ORT_TENSORRT_FP16_ENABLE", fp16);
                Environment.SetEnvironmentVariable("ORT_TENSORRT_ENGINE_CACHE_ENABLE", "1");
                Environment.SetEnvironmentVariable("ORT_TENSORRT_CACHE_PATH", options.TensorRtEngineCachePath);
                Environment.SetEnvironmentVariable("ORT_TENSORRT_TIMING_CACHE_ENABLE", "1");

                sessionOptions.AppendExecutionProvider_Tensorrt(options.GpuId);
                tensorRtAppended = true;
                VisionBackendResolver.SetEffectiveProvider("TensorRT");

                logger.LogInformation(
                    "TensorRT execution provider appended: gpuId={GpuId}, fp16={Fp16}, cache={CachePath}.",
                    options.GpuId, fp16 == "1", options.TensorRtEngineCachePath);
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
