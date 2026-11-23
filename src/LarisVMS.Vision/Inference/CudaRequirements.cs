using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LarisVMS.Vision.Inference;

/// <summary>
/// What the bundled ONNX Runtime CUDA build needs on an NVIDIA node, in one place: the minimum driver
/// for its CUDA version, and the CUDA and cuDNN major versions. The README's and Help's GPU
/// requirements tables quote these values; change them together whenever the ONNX Runtime packages
/// in Directory.Packages.props move (ORT 1.24.x is built with CUDA 12.8, which needs driver 570.26 on
/// Linux and 570.65 on Windows — NVIDIA's CUDA 12.8 GA release notes).
/// </summary>
public static partial class CudaRequirements
{
    public const string CudaVersion = "12.8";
    public const int CudnnMajor = 9;
    public static readonly Version MinDriverLinux = new(570, 26);
    public static readonly Version MinDriverWindows = new(570, 65);

    public static Version MinDriver => OperatingSystem.IsWindows() ? MinDriverWindows : MinDriverLinux;

    /// <summary>The installed NVIDIA driver version, or null when there's no NVIDIA driver or it can't
    /// be read. Linux reads the kernel module's version; Windows asks nvidia-smi (installed with the
    /// driver, on PATH).</summary>
    public static Version? InstalledDriverVersion()
    {
        try
        {
            if (OperatingSystem.IsLinux())
                return File.Exists("/proc/driver/nvidia/version") ? ParseProcVersion(File.ReadAllText("/proc/driver/nvidia/version")) : null;
            if (OperatingSystem.IsWindows())
                return ParseNvidiaSmi(RunNvidiaSmi());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                                       or InvalidOperationException)
        {
            // No driver, or nothing readable: unknown.
        }
        return null;
    }

    /// <summary>"NVRM version: NVIDIA UNIX x86_64 Kernel Module  570.86.15  Thu ..." → 570.86.15.</summary>
    internal static Version? ParseProcVersion(string text)
    {
        var match = KernelModuleVersion().Match(text);
        return match.Success && Version.TryParse(match.Groups[1].Value, out var version) ? version : null;
    }

    /// <summary>nvidia-smi's "--query-gpu=driver_version --format=csv,noheader" output: one version per
    /// GPU ("572.16"); the first is used (one driver serves every GPU).</summary>
    internal static Version? ParseNvidiaSmi(string? output)
    {
        var first = output?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return first is not null && Version.TryParse(first, out var version) ? version : null;
    }

    /// <summary>Null when <paramref name="installed"/> meets <paramref name="minimum"/> (or is
    /// unknown); otherwise the note for the Nodes page.</summary>
    internal static string? DriverTooOldNote(Version? installed, Version minimum) =>
        installed is not null && installed < minimum
            ? $"the NVIDIA driver is {installed}, but CUDA {CudaVersion} needs {minimum} or newer — update the NVIDIA driver for CUDA acceleration"
            : null;

    private static string? RunNvidiaSmi()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "nvidia-smi",
            ArgumentList = { "--query-gpu=driver_version", "--format=csv,noheader" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process is null) return null;
        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(5000))
        {
            try { process.Kill(); } catch (InvalidOperationException) { /* already gone */ }
            return null;
        }
        return process.ExitCode == 0 ? output : null;
    }

    [GeneratedRegex(@"Kernel Module\s+(\d+\.\d+(?:\.\d+)?)")]
    private static partial Regex KernelModuleVersion();
}
