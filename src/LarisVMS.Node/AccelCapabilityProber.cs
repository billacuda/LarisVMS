using LarisVMS.Core.Enums;

namespace LarisVMS.Node;

/// <summary>
/// Probes this node's own hardware for which AI-detection accelerators are actually present —
/// object detection plan decision 2's local half of accelerator resolution, mirroring
/// FfmpegCapabilityProber's own shape exactly: probe once at node startup, cache, report on every
/// heartbeat via Node.DetectedAcceleratorsJson, never itself the thing a node acts on (see
/// AccelSelection for how the node's own resolved value actually gets used — always its own
/// freshest local probe, never a value fetched back from the server).
///
/// GPU vendor is read from Win32_VideoController (WMI) — Windows-only, matching every other
/// hardware-facing probe in this app (Node itself is Windows-only today). This reports which
/// vendor's display adapter is present, a reasonable proxy for "an execution provider for that
/// vendor is worth trying," but not a guarantee one will actually bind — Nvidia's CUDA path still
/// needs cuDNN discoverable, Intel/AMD's OpenVINO/DirectML paths need their own runtime
/// prerequisites this probe doesn't verify (the same caveat EngineFactory's own PrependToPath doc
/// comment already makes about cuDNN/TensorRT specifically — detected hardware is a necessary, not
/// sufficient, signal).
/// </summary>
public static class AccelCapabilityProber
{
    /// <summary>Never throws — a probe failure (WMI unavailable, non-Windows, permissions) just
    /// means this node reports no detected accelerators this run, the same "capability detection
    /// failing must not be able to stop the node from starting" stance FfmpegCapabilityProber's own
    /// doc comment already takes.</summary>
    public static Task<IReadOnlyList<AiAccelerator>> ProbeAsync()
    {
        if (!OperatingSystem.IsWindows()) return Task.FromResult<IReadOnlyList<AiAccelerator>>([]);

        try
        {
            return Task.FromResult(ParseAccelerators(QueryVideoControllerNames()));
        }
        catch
        {
            return Task.FromResult<IReadOnlyList<AiAccelerator>>([]);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static List<string> QueryVideoControllerNames()
    {
        using var searcher = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
        var names = new List<string>();
        foreach (var obj in searcher.Get())
        {
            if (obj["Name"] is string name && !string.IsNullOrWhiteSpace(name)) names.Add(name);
        }
        return names;
    }

    /// <summary>Pure parsing, extracted so it's testable without touching WMI — same
    /// internal-for-testability pattern FfmpegCapabilityProber.ParseEncodersOutput uses (public
    /// here rather than internal, since there's no ByteTrack-style encapsulation reason to hide
    /// it). A machine can report more than one vendor (an Intel iGPU alongside a discrete NVIDIA
    /// card is common), which is exactly why AccelSelection's own Auto priority order exists —
    /// this just reports everything actually present.</summary>
    public static IReadOnlyList<AiAccelerator> ParseAccelerators(IReadOnlyList<string> videoControllerNames)
    {
        var result = new List<AiAccelerator>();
        if (videoControllerNames.Any(n => n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)))
            result.Add(AiAccelerator.Nvidia);
        if (videoControllerNames.Any(n => n.Contains("Intel", StringComparison.OrdinalIgnoreCase)))
            result.Add(AiAccelerator.Intel);
        if (videoControllerNames.Any(n => n.Contains("AMD", StringComparison.OrdinalIgnoreCase) || n.Contains("Radeon", StringComparison.OrdinalIgnoreCase)))
            result.Add(AiAccelerator.Amd);
        return result;
    }
}
