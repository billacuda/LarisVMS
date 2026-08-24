using Microsoft.Extensions.Logging;
using YoloDotNet.Models.Interfaces;

#if ACCEL_CUDA
using YoloDotNet.ExecutionProvider.Cuda;
using YoloDotNet.ExecutionProvider.Cuda.TensorRT;
#elif ACCEL_DIRECTML
using YoloDotNet.ExecutionProvider.DirectML;
#elif ACCEL_OPENVINO
using YoloDotNet.ExecutionProvider.OpenVino;
#else
using YoloDotNet.ExecutionProvider.Cpu;
#endif

namespace LarisVMS.Vision.Inference;

/// <summary>
/// Builds the <see cref="IExecutionProvider"/> for whichever accelerator this build was compiled
/// for (see Accel in LarisVMS.Vision.csproj). This is the only file in the project with conditional
/// compilation -- everything else is written once against YoloDotNet's model-agnostic API and
/// doesn't need to know which provider is underneath.
///
/// Referencing more than one YoloDotNet.ExecutionProvider.* package in the same build causes
/// native DLL conflicts, which is why the choice is a compile-time symbol rather than a runtime
/// branch: it's structurally impossible to end up with two providers linked in at once. This is
/// also why LarisVMS.Vision.Service is published once per accelerator (see the detection plan's
/// decision 2) rather than trying to switch providers inside one running process.
///
/// Ported near-verbatim from aitest (g:\Projects\aitest\src\Aitest.Vision\Inference\EngineFactory.cs).
/// </summary>
public static class EngineFactory
{
    public static IExecutionProvider Create(EngineOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

#if ACCEL_CUDA
        PrependToPath(options.CudnnPath, "cudnn64_9.dll", "cuDNN", logger);

        TensorRt? trtConfig = null;
        if (options.EnableTensorRt)
        {
            PrependToPath(options.TensorRtLibPath, "nvinfer_10.dll", "TensorRT", logger);

            if (string.IsNullOrWhiteSpace(options.TensorRtEngineCachePath))
            {
                throw new InvalidOperationException(
                    "EnableTensorRt requires TensorRtEngineCachePath -- without a cache, every " +
                    "process start rebuilds the engine from scratch, which can take minutes.");
            }

            var precision = Enum.Parse<TrtPrecision>(options.TensorRtPrecision, ignoreCase: true);

            trtConfig = new TensorRt
            {
                Precision = precision,
                EngineCachePath = options.TensorRtEngineCachePath,
                EngineCachePrefix = "larisvms-vision",
            };

            logger.LogInformation(
                "CUDA execution provider: gpuId={GpuId}, TensorRT enabled ({Precision}, cache={CachePath}).",
                options.GpuId, precision, options.TensorRtEngineCachePath);
        }
        else
        {
            logger.LogInformation("CUDA execution provider: gpuId={GpuId}, TensorRT disabled.", options.GpuId);
        }

        return new CudaExecutionProvider(options.ModelPath, options.GpuId, trtConfig);
#elif ACCEL_DIRECTML
        logger.LogInformation("DirectML execution provider: gpuId={GpuId}.", options.GpuId);
        return new DirectMLExecutionProvider(options.ModelPath, options.GpuId);
#elif ACCEL_OPENVINO
        logger.LogInformation("OpenVINO execution provider: device={Device}.", options.OpenVinoDeviceType);
        return new OpenVinoExecutionProvider(options.ModelPath, new OpenVino
        {
            DeviceType = options.OpenVinoDeviceType,
        });
#else
        logger.LogInformation("CPU execution provider.");
        return new CpuExecutionProvider(options.ModelPath);
#endif
    }

#if ACCEL_CUDA
    private static readonly HashSet<string> _appliedPaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock _pathLock = new();

    /// <summary>
    /// Prepends a directory to the process PATH, if configured. Must run before the CUDA execution
    /// provider is constructed -- ONNX Runtime resolves native libraries via the OS loader on
    /// first use, and Windows' LoadLibrary search reads PATH live at call time, so mutating it
    /// here (immediately before construction, in the same code path) is early enough.
    ///
    /// This exists because neither cuDNN nor TensorRT install like a normal Windows package: cuDNN
    /// via `pip install nvidia-cudnn-cu12` lands under a Python environment's site-packages, and
    /// TensorRT is a standalone SDK zip you extract wherever you like -- neither is somewhere the
    /// OS loader searches by default. A system-wide install doesn't need this, which is why both
    /// paths are optional.
    ///
    /// If the configured directory doesn't contain the expected DLL, this logs a warning rather
    /// than throwing: the CUDA execution provider will still attempt to load, ONNX Runtime will
    /// still fail to find the library, and it will silently fall back to CPU. That fallback has no
    /// exception to catch -- the warning here is the only signal before an operator notices the
    /// app is unexpectedly slow.
    /// </summary>
    private static void PrependToPath(string? directory, string expectedDll, string label, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        lock (_pathLock)
        {
            if (!_appliedPaths.Add(directory))
            {
                return;
            }

            if (!File.Exists(Path.Combine(directory, expectedDll)))
            {
                logger.LogWarning(
                    "{Label} path '{Path}' does not contain {Dll}. The CUDA execution provider " +
                    "will likely fail to find {Label} and ONNX Runtime will silently fall back to " +
                    "CPU -- no exception will be thrown for this. Verify the path.",
                    label, directory, expectedDll, label);
            }

            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            if (!currentPath.Split(Path.PathSeparator).Contains(directory, StringComparer.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + currentPath);
                logger.LogInformation("Prepended '{Path}' to PATH for {Label}.", directory, label);
            }
        }
    }
#endif
}
