using Microsoft.Extensions.Logging;

namespace LarisVMS.Vision.Inference;

/// <summary>
/// Was the YoloDotNet execution-provider builder (see git history) — with YoloEngine deleted (the
/// D-FINE integration bypasses YoloDotNet's own decoders entirely, see DFineEngine's doc comment),
/// nothing constructs a YoloDotNet IExecutionProvider anymore. Kept only for <see
/// cref="PrependToPath"/>, which OrtSessionFactory's own CUDA/TensorRT setup still needs — the
/// reasoning below is unrelated to YoloDotNet and applies identically to any ONNX Runtime CUDA
/// execution provider, whichever engine constructs it.
/// </summary>
public static class EngineFactory
{
#if ACCEL_CUDA
    private static readonly HashSet<string> _appliedPaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock _pathLock = new();
#endif

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
    internal static void PrependToPath(string? directory, string expectedDll, string label, ILogger logger)
    {
#if ACCEL_CUDA
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
#else
        // Inert outside a CUDA build -- no other accelerator needs a PATH mutation for its native
        // libraries (DirectML/OpenVINO/CPU all resolve theirs the normal way).
#endif
    }
}
