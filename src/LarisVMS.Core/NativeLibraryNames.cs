namespace LarisVMS.Core;

/// <summary>
/// File names of the native libraries the AI backends load, per OS: a Windows DLL name or the Linux
/// shared-object name with its ABI version ("cudart64_12.dll" / "libcudart.so.12"). The versions match
/// what the bundled ONNX Runtime build was compiled against (CUDA 12, cuDNN 9, TensorRT 10); see
/// CudaRequirements for the matching driver minimums.
/// </summary>
public static class NativeLibraryNames
{
    private static string Pick(string windows, string linux) => OperatingSystem.IsWindows() ? windows : linux;

    /// <summary>The ONNX Runtime library in a backend folder.</summary>
    public static string OnnxRuntime => Pick("onnxruntime.dll", "libonnxruntime.so");

    /// <summary>The CUDA execution provider inside the cuda backend folder (fetched from the server).</summary>
    public static string OnnxRuntimeCudaProvider => Pick("onnxruntime_providers_cuda.dll", "libonnxruntime_providers_cuda.so");

    /// <summary>The CUDA 12 runtime.</summary>
    public static string CudaRuntime => Pick("cudart64_12.dll", "libcudart.so.12");

    /// <summary>cuDNN 9 (a dispatcher for the other cudnn libraries beside it).</summary>
    public static string Cudnn => Pick("cudnn64_9.dll", "libcudnn.so.9");

    /// <summary>TensorRT 10.</summary>
    public static string TensorRt => Pick("nvinfer_10.dll", "libnvinfer.so.10");

    /// <summary>The name to hand NativeLibrary.TryLoad so the OS searches its own paths: the DLL name
    /// without ".dll" on Windows, the full versioned name on Linux (dlopen's search needs it).</summary>
    public static string LoadName(string fileName) =>
        OperatingSystem.IsWindows() && fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? fileName[..^4] : fileName;
}
