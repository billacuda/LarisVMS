using System.Diagnostics;
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
public sealed class VisionServiceSupervisor(string nodeInstallDirectory, ILogger<VisionServiceSupervisor> logger)
{
    public const string ExeFileName = "LarisVMS.Vision.Service.exe";

    // Loopback-only, matching Vision Service's own default (VisionServiceOptions.Port) — fixed
    // rather than negotiated, since exactly one instance ever runs per node.
    public const int Port = 5990;

    private Process? _process;
    private bool _warnedMissingExe;

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

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
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
