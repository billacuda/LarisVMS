using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace LarisVMS.Vision.Inference;

/// <summary>
/// Builds a <see cref="SessionOptions"/> configured for whichever accelerator this build was
/// compiled for (see Accel in LarisVMS.Vision.csproj) -- the raw-ONNX-Runtime sibling of
/// EngineFactory.Create, for engines (DFineEngine, and any future first-party decoder) that talk
/// to Microsoft.ML.OnnxRuntime directly instead of going through YoloDotNet's own IExecutionProvider
/// wrapper types. Same conditional-compilation shape and the same reasoning for why the choice is a
/// compile-time symbol rather than a runtime branch: referencing more than one native ONNX Runtime
/// backend in the same build risks the same native-DLL conflicts YoloDotNet's own execution-provider
/// packages already avoid by construction.
///
/// EngineOptions is shared with the (now YoloDotNet-only) EngineFactory rather than a new options
/// type -- both configure the same handful of concerns (GPU id, cuDNN/TensorRT paths, OpenVINO
/// device) against the same physical hardware, so a second parallel options class would just be two
/// places that could disagree about the same machine.
/// </summary>
public static class OrtSessionFactory
{
    public static SessionOptions Create(EngineOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var sessionOptions = new SessionOptions();

#if ACCEL_CUDA
        EngineFactory.PrependToPath(options.CudnnPath, "cudnn64_9.dll", "cuDNN", logger);

        if (options.EnableTensorRt)
        {
            EngineFactory.PrependToPath(options.TensorRtLibPath, "nvinfer_10.dll", "TensorRT", logger);

            if (string.IsNullOrWhiteSpace(options.TensorRtEngineCachePath))
            {
                throw new InvalidOperationException(
                    "EnableTensorRt requires TensorRtEngineCachePath -- without a cache, every " +
                    "process start rebuilds the engine from scratch, which can take minutes.");
            }

            // Registered via the generic string-keyed provider-options overload rather than
            // OrtTensorRTProviderOptions (a SafeHandle-backed type with no usable public
            // constructor for setting these from managed code) -- these option names are ONNX
            // Runtime's own documented TensorRT execution provider keys, not LarisVMS-specific.
            var fp16 = options.TensorRtPrecision.Equals("FP16", StringComparison.OrdinalIgnoreCase) ? "1" : "0";
            sessionOptions.AppendExecutionProvider("Tensorrt", new Dictionary<string, string>
            {
                ["device_id"] = options.GpuId.ToString(),
                ["trt_fp16_enable"] = fp16,
                ["trt_engine_cache_enable"] = "1",
                ["trt_engine_cache_path"] = options.TensorRtEngineCachePath,
            });

            logger.LogInformation(
                "TensorRT execution provider appended: gpuId={GpuId}, fp16={Fp16}, cache={CachePath}.",
                options.GpuId, fp16 == "1", options.TensorRtEngineCachePath);
        }

        // Appended after TensorRT (when enabled) as the fallback for any op TensorRT doesn't
        // support -- ONNX Runtime tries providers in append order, first match wins per op.
        sessionOptions.AppendExecutionProvider_CUDA(options.GpuId);
        logger.LogInformation("CUDA execution provider: gpuId={GpuId}.", options.GpuId);
#elif ACCEL_DIRECTML
        sessionOptions.AppendExecutionProvider_DML(options.GpuId);
        logger.LogInformation("DirectML execution provider: gpuId={GpuId}.", options.GpuId);
#elif ACCEL_OPENVINO
        sessionOptions.AppendExecutionProvider_OpenVINO(options.OpenVinoDeviceType);
        logger.LogInformation("OpenVINO execution provider: device={Device}.", options.OpenVinoDeviceType);
#else
        sessionOptions.AppendExecutionProvider_CPU(1);
        logger.LogInformation("CPU execution provider.");
#endif

        return sessionOptions;
    }
}
