using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace LarisVMS.Node;

/// <summary>
/// Supervises the co-deployed LarisVMS.Vision.Service.exe sibling process (object detection plan
/// decisions 2/3) — started only when at least one assigned camera has AI detection enabled and
/// this node has successfully resolved an accelerator to use (AccelSelection.Choose), stopped
/// otherwise. Published once per accelerator (Cuda/DirectML/OpenVino/Cpu), but exactly one variant
/// is ever installed on a given machine, always under the same file name — which build a site
/// actually ships is a publish-time choice (build-node.ps1), not something this node needs to know
/// or select between at runtime.
///
/// Checked from NodeWorker's own 30s reconcile tick, not a dedicated supervision loop of its own —
/// a crash between two ticks is an acceptable detection window for a service that's already
/// best-effort/additive by design (see AccelSelection's own "null means don't start it at all, every
/// other detection path stays unaffected" reasoning).
/// </summary>
public sealed class VisionServiceSupervisor(string nodeInstallDirectory, string ffmpegPath, ILogger<VisionServiceSupervisor> logger)
{
    public const string ExeFileName = "LarisVMS.Vision.Service.exe";

    // Loopback-only, matching Vision Service's own default (VisionServiceOptions.Port) — fixed
    // rather than negotiated, since exactly one instance ever runs per node.
    public const int Port = 5990;

    private Process? _process;
    private bool _warnedMissingExe;

    // Windows job object holding the child, created once and deliberately never closed for the
    // lifetime of this process. Every process in a job configured with KILL_ON_JOB_CLOSE is
    // terminated by the OS when the last handle to that job closes — and the handle this field holds
    // is the only one, so it closes exactly when LarisVMS.Node exits, however it exits.
    //
    // Stop() below is still the ordinary path and still runs first at shutdown. This exists for the
    // paths no amount of shutdown code can cover: the host's ShutdownTimeout firing before the
    // cleanup gets its turn, a crash, a taskkill, the SCM giving up on a hung stop. Before this, any
    // of those left LarisVMS.Vision.Service.exe running with the node gone — holding its own exe and
    // the CUDA/cuDNN natives beside it open, which then fails the next in-place upgrade with "being
    // used by another process" (confirmed on a real node, and why install-node.ps1 has to hunt down
    // orphans before copying at all).
    private IntPtr _jobHandle = IntPtr.Zero;
    private bool _warnedJobObjectUnavailable;

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Starts the process if it isn't already running, or restarts it if it exited on its
    /// own since the last check.</summary>
    public void EnsureRunning()
    {
        if (_process is { HasExited: false }) return;

        if (_process is { HasExited: true })
        {
            logger.LogWarning("LarisVMS.Vision.Service exited unexpectedly (code {ExitCode}) — restarting.", _process.ExitCode);
            _process.Dispose();
            _process = null;
        }

        var exePath = Path.Combine(nodeInstallDirectory, ExeFileName);
        if (!File.Exists(exePath))
        {
            if (!_warnedMissingExe)
            {
                _warnedMissingExe = true;
                logger.LogWarning(
                    "AI detection is enabled for at least one camera on this node, but {ExePath} was not " +
                    "found alongside LarisVMS.Node.exe — AI detection will not run until it's installed. " +
                    "Every other detection path already configured for affected cameras is unaffected.",
                    exePath);
            }
            return;
        }
        _warnedMissingExe = false;

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = nodeInstallDirectory
        };
        // ASP.NET Core's own configuration-from-environment-variables convention (double underscore
        // = section separator) — no shared config file between the two processes needed.
        psi.EnvironmentVariables["Vision__Port"] = Port.ToString();
        // Without this, VisionServiceOptions.FfmpegPath is unset and FfmpegPathResolver falls back to
        // a bare "ffmpeg", relying on PATH — which this service, normally running as LocalSystem,
        // does not have (confirmed live: "Win32Exception: cannot find the file specified" starting
        // VisionSession's own ffmpeg). Node already resolved a real path at its own startup (its
        // --ffmpeg-path arg, LARISVMS_FFMPEG_PATH, or a PATH probe run once as this same account) —
        // passed straight through rather than making the child re-derive it, so the two processes can
        // never disagree about which ffmpeg they're each running.
        psi.EnvironmentVariables["Vision__FfmpegPath"] = ffmpegPath;

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
        AssignToKillOnCloseJob(process);
        _ = DrainOutputAsync(process.StandardOutput, "stdout");
        _ = DrainOutputAsync(process.StandardError, "stderr");

        _process = process;
        logger.LogInformation("Started LarisVMS.Vision.Service (PID {Pid}) on port {Port}.", process.Id, Port);
    }

    public void Stop()
    {
        if (_process is not { HasExited: false })
        {
            _process?.Dispose();
            _process = null;
            return;
        }

        try
        {
            _process.Kill(entireProcessTree: true);
            // Kill() only initiates termination — it returns before the OS has necessarily finished
            // tearing the process down and released its own open file handles (the exe itself,
            // onnxruntime.dll, ...). Bounded rather than unbounded: this runs inline in NodeWorker's
            // own shutdown path (see ExecuteAsync's finally block), which the Windows Service Control
            // Manager is itself waiting on before it reports this service Stopped — a hang here would
            // otherwise hang the whole node's shutdown. 5s is generous for a Kill() to actually land;
            // if it's not enough, auto-update's own Vision Service binary swap (which only proceeds
            // once the SCM reports Stopped) may transiently fail with a file-in-use error and just
            // retry on the next successful update offer, the same "best-effort, never fatal" contract
            // NodeUpdater's own Vision swap step already documents.
            _process.WaitForExit(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) { logger.LogDebug(ex, "Failed to stop LarisVMS.Vision.Service (may have already exited)."); }

        _process.Dispose();
        _process = null;
        logger.LogInformation("Stopped LarisVMS.Vision.Service.");
    }

    /// <summary>Puts the just-started child into this supervisor's kill-on-close job object, creating
    /// the job on first use. Entirely best-effort: every failure here leaves the process running
    /// normally and only gives up the "dies with the parent no matter what" guarantee, falling back
    /// to <see cref="Stop"/>'s explicit Kill — which is what this class did before the job object
    /// existed. Warned about once rather than per start, since it would otherwise repeat on every
    /// restart of a crash-looping child.</summary>
    private void AssignToKillOnCloseJob(Process process)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            if (_jobHandle == IntPtr.Zero)
            {
                var job = CreateJobObjectW(IntPtr.Zero, null);
                if (job == IntPtr.Zero) throw new Win32Exception_(Marshal.GetLastWin32Error(), "CreateJobObject");

                var limits = new JobObjectExtendedLimitInformation
                {
                    BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose }
                };
                var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);
                    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, buffer, (uint)size))
                    {
                        var error = Marshal.GetLastWin32Error();
                        CloseHandle(job);
                        throw new Win32Exception_(error, "SetInformationJobObject");
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }

                _jobHandle = job;
            }

            // Nested jobs are supported on Windows 8 / Server 2012 and later, so this still works if
            // the node's own service host already placed this process in a job of its own.
            if (!AssignProcessToJobObject(_jobHandle, process.Handle))
                throw new Win32Exception_(Marshal.GetLastWin32Error(), "AssignProcessToJobObject");
        }
        catch (Exception ex)
        {
            if (!_warnedJobObjectUnavailable)
            {
                _warnedJobObjectUnavailable = true;
                logger.LogWarning(ex,
                    "Could not place LarisVMS.Vision.Service in a kill-on-close job object — it will still be " +
                    "stopped normally when this node shuts down cleanly, but may survive as an orphan if this " +
                    "process is killed or times out during shutdown.");
            }
        }
    }

    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    /// <summary>Named with a trailing underscore purely to avoid colliding with
    /// System.ComponentModel.Win32Exception, which isn't imported here and would pull a using in for
    /// three throw sites.</summary>
    private sealed class Win32Exception_(int error, string api)
        : InvalidOperationException($"{api} failed (Win32 error {error}).");

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInformationClass,
        IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private async Task DrainOutputAsync(StreamReader reader, string streamName)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
                logger.LogDebug("vision-service ({Stream}): {Line}", streamName, line);
        }
        catch { /* process exited, stream closed — nothing more to drain */ }
    }
}
