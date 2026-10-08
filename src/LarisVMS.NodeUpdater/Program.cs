// LarisVMS.NodeUpdater.exe — swap the recorder node binary while the Windows Service is stopped.
//
// Usage (launched automatically by LarisVMS.Node, as a detached process, right before it calls
// IHostApplicationLifetime.StopApplication() on itself — see LarisVMS.Node/Update/UpdateService.cs):
//   LarisVMS.NodeUpdater.exe --new <staged> --current <running-binary> --service <service-name>
//
// Direct port of dploid.AgentUpdater/Program.cs's Windows path — see that file's own doc comment for
// the shape this mirrors. LarisVMS.Node has no Linux build (see NodeConfigStore's doc comment), so
// unlike dploid's updater there is no Linux branch to port here at all.
//
// Steps:
//   1. Wait until the service reaches Stopped state (max 60s) — LarisVMS.Node.exe holds its own file
//      open while running, so the swap below can't happen until the SCM has actually released it.
//   2. Back up the current binary, move the new one over it (UpdaterLogic.TrySwapBinary), retrying
//      while the stopped process is still releasing it. Current nodes and proxies swap their own
//      binary before stopping (LarisVMS.Core.Update.InPlaceBinarySwap) and launch this with
//      --restart-only, so this step is only the fallback when that rename fails.
//   3. Start the service again (with a few retries — see StartServiceWithRetries) and re-apply the
//      same failure-recovery config install-node.ps1 sets at install time — a raw binary swap doesn't
//      touch service configuration, but re-applying here is cheap insurance against a service that
//      was ever re-registered without it.
//
// Logging: this process runs detached from a Windows Service with no console session, so plain
// Console output — everything below used to be — went nowhere the one time this actually mattered:
// a failed update left the service stopped with zero trace of why. Log() below writes the same lines
// both to Console (harmless, and still useful if anyone ever runs this by hand) and to a rolling daily
// file next to LarisVMS.Node's own log directory, so a future failure here is actually diagnosable
// from the node machine afterward instead of relying on catching it live.

using System.Diagnostics;
using LarisVMS.NodeUpdater;

const int MaxWaitSeconds = 60;
// A first attempt failing here isn't necessarily the final word — "the service" as sc.exe sees it
// can still be finishing a SERVICE_STOPPED -> fully released transition, or a security product can
// hold a momentary lock on the freshly-swapped exe, for a few seconds after IsServiceStopped already
// reported STOPPED above. Confirmed as a real gap live: the one time this update path failed, nothing
// in this process's own control flow ever retried the start step — it was exactly one shot.
const int MaxStartAttempts = 5;
const int StartRetryDelaySeconds = 10;
// After the process has exited, a security product can still hold the exe briefly.
const int MaxSwapAttempts = 10;
const int SwapRetryDelaySeconds = 3;

var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LarisVMS", "logs");

var parsed = UpdaterLogic.ParseArgs(args);

// --restart-only: no binary swap, just outlive the service and bring it back (the Nodes page's own
// "Restart service" button). Everything below is written as "wait for stopped → [swap] → start", so
// this mode only skips the middle step; the two halves that matter are shared verbatim.
if (!parsed.RestartOnly)
{
    if (parsed.NewBinary is null || parsed.CurrentBinary is null)
    {
        Log("ERROR", "Usage: LarisVMS.NodeUpdater.exe --new <path> --current <path> [--service <name>]");
        Log("ERROR", "   or: LarisVMS.NodeUpdater.exe --restart-only [--service <name>]");
        return 1;
    }

    if (!File.Exists(parsed.NewBinary))
    {
        Log("ERROR", $"Staged binary not found: {parsed.NewBinary}");
        return 1;
    }
}

// ── Wait for service to stop ───────────────────────────────────────────────

Log("INFO", $"Waiting for service '{parsed.ServiceName}' to stop...");

var stopped = false;
for (var i = 0; i < MaxWaitSeconds * 2; i++)
{
    if (IsServiceStopped(parsed.ServiceName))
    {
        stopped = true;
        break;
    }
    await Task.Delay(500);
}

if (!stopped)
{
    Log("ERROR", $"Service '{parsed.ServiceName}' did not stop within {MaxWaitSeconds}s. Aborting — the service is " +
        "left stopped and will need a manual `sc start` (or a re-run of install-node.ps1's service registration).");
    return 1;
}

Log("INFO", "Service stopped.");

// ── Swap binary ────────────────────────────────────────────────────────────

// The SCM reports Stopped before the service's process has actually exited, so its exe can still be
// locked for a moment ("Access to the path is denied"). Wait for the process itself, then retry the
// swap for a while before giving up on it.
var swapFailed = false;
if (parsed.RestartOnly)
{
    Log("INFO", "Restart-only mode — skipping binary swap.");
}
else
{
    await WaitForProcessExitAsync(Path.GetFileNameWithoutExtension(parsed.CurrentBinary!));

    string? swapError = null;
    var swapped = false;
    for (var attempt = 1; attempt <= MaxSwapAttempts && !swapped; attempt++)
    {
        swapped = UpdaterLogic.TrySwapBinary(parsed.NewBinary!, parsed.CurrentBinary!, out swapError);
        if (!swapped && attempt < MaxSwapAttempts) await Task.Delay(TimeSpan.FromSeconds(SwapRetryDelaySeconds));
    }

    if (swapped)
    {
        Log("INFO", $"Binary swapped: {parsed.NewBinary} -> {parsed.CurrentBinary}");
    }
    else
    {
        // Start it again on the previous binary rather than leaving a recorder down; the next update
        // offer tries again.
        swapFailed = true;
        Log("ERROR", $"Failed to swap binary after {MaxSwapAttempts} attempts: {swapError}. Starting the service " +
            "again on its previous binary (a '.bak' may be sitting next to it).");
    }
}

// Object detection plan follow-up: optional second swap, only attempted when UpdateService staged
// one (both fields non-null — see ParsedArgs' own doc comment) AND this machine already has a
// working Vision Service install (native onnxruntime.dll etc. + models\, both placed by
// install-node.ps1, never by this auto-update path — it only ever swaps the one .exe). Swapping the
// .exe onto a machine that never had those is worse than not swapping at all: a lone exe with no
// native deps to load fails to start and crash-loops noisily under VisionServiceSupervisor's own
// restart-on-exit logic, rather than the clear "not installed" state it's in today. Existence of the
// *current* Vision exe is the signal this uses for "already fully installed" — a real install-node.ps1
// run always places the exe and its dependencies together, so if the exe is there, so is everything
// else it needs.
//
// Deliberately best-effort and never fatal to the overall update: a failure here is logged and left
// for the next successful heartbeat's update offer to retry, exactly like a failed *download* of it
// already is on the Node side — the required Node swap and the service restart below proceed
// regardless, same "additive, never blocks recording" reasoning this whole feature follows
// throughout. LarisVMS.Vision.Service isn't managed by the SCM the way the Node service is, so
// there's no separate "wait for it to stop" step needed here — NodeWorker's own shutdown
// (VisionServiceSupervisor.Stop, with its own bounded wait for the process to actually exit) already
// ran to completion before the Node service itself could report Stopped above.
if (!parsed.RestartOnly && !swapFailed && parsed.NewVisionBinary is not null && parsed.CurrentVisionBinary is not null)
{
    if (!File.Exists(parsed.CurrentVisionBinary))
    {
        Log("INFO", $"Vision Service update available, but {parsed.CurrentVisionBinary} isn't installed on this " +
            "machine yet (its native dependencies wouldn't be either) — skipping. Run install-node.ps1 once to " +
            "install AI detection on this node; auto-update only keeps an already-installed copy current.");
    }
    else if (!UpdaterLogic.TrySwapBinary(parsed.NewVisionBinary, parsed.CurrentVisionBinary, out var visionSwapError))
    {
        Log("WARN", $"Failed to swap Vision Service binary: {visionSwapError}. AI detection stays on its previous " +
            "binary until the next successful update — the Node service itself is unaffected and starts normally.");
    }
    else
    {
        Log("INFO", $"Vision Service binary swapped: {parsed.NewVisionBinary} -> {parsed.CurrentVisionBinary}");
    }
}

// ── Start service ──────────────────────────────────────────────────────────

if (!await StartServiceWithRetriesAsync(parsed.ServiceName))
{
    Log("ERROR", $"Giving up starting '{parsed.ServiceName}' after {MaxStartAttempts} attempts. " +
        $"The service is left stopped — start it manually (`sc start {parsed.ServiceName}`) and check its own " +
        "Windows Event Log entries for why sc.exe itself is refusing.");
    return 1;
}

Log("INFO", "Service started.");

// Re-apply failure recovery in case the service was re-registered — same config install-node.ps1
// sets at install time (see that script's own "configure service recovery" step). Best-effort:
// the service is already back up at this point, so a failure here shouldn't be reported as an
// overall failure of the update.
try { RunCommand("sc.exe", $"failure {parsed.ServiceName} reset= 86400 actions= restart/60000/restart/60000/restart/60000"); }
catch (Exception ex) { Log("WARN", $"Could not re-apply service failure-recovery config: {ex.Message}"); }
try { RunCommand("sc.exe", $"failureflag {parsed.ServiceName} 1"); }
catch (Exception ex) { Log("WARN", $"Could not re-apply service failureflag: {ex.Message}"); }

return swapFailed ? 1 : 0;

// ── Wait for the stopped service's process to actually exit ───────────────────────────────────

async Task WaitForProcessExitAsync(string processName)
{
    var deadline = DateTime.UtcNow.AddSeconds(MaxWaitSeconds);
    foreach (var process in Process.GetProcessesByName(processName))
    {
        using (process)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            Log("INFO", $"Waiting for {processName} (PID {process.Id}) to exit...");
            using var cts = new CancellationTokenSource(remaining);
            try { await process.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { Log("WARN", $"{processName} (PID {process.Id}) is still running; trying the swap anyway."); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* exited or inaccessible */ }
        }
    }
}

// ── Start-with-retries (the one step that used to be single-shot) ──────────────────────────────

async Task<bool> StartServiceWithRetriesAsync(string serviceName)
{
    for (var attempt = 1; attempt <= MaxStartAttempts; attempt++)
    {
        Log("INFO", $"Starting service '{serviceName}' (attempt {attempt}/{MaxStartAttempts})...");
        try
        {
            RunCommand("sc.exe", $"start {serviceName}");
            return true;
        }
        catch (Exception ex)
        {
            Log("WARN", $"Attempt {attempt}/{MaxStartAttempts} to start '{serviceName}' failed: {ex.Message}");
            if (attempt < MaxStartAttempts) await Task.Delay(TimeSpan.FromSeconds(StartRetryDelaySeconds));
        }
    }
    return false;
}

// ── Helpers (sc.exe-dependent — not unit tested, see UpdaterLogic's doc comment) ────────────────

static bool IsServiceStopped(string name)
{
    try
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = $"query {name}",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(5000);
        return output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase);
    }
    catch { return false; }
}

static void RunCommand(string exe, string args)
{
    using var proc = Process.Start(new ProcessStartInfo
    {
        FileName = exe,
        Arguments = args,
        UseShellExecute = false,
        CreateNoWindow = true,
    })!;
    proc.WaitForExit(15_000);
    if (proc.ExitCode != 0)
        throw new Exception($"{exe} exited with code {proc.ExitCode}");
}

// Writes both to Console (still useful if this is ever run by hand from a terminal) and to a
// rolling daily file next to LarisVMS.Node's own logs — same directory, same one-file-per-day
// convention as LarisVMS.Core.Logging.FileLoggerProvider, kept as a small standalone copy here
// rather than a project reference so this exe stays the minimal, dependency-free binary it's
// designed to be (see this file's own top comment). This is the fix for a real incident: this
// process runs detached from a Windows Service with no console session, so before this existed,
// whatever went wrong here — a stop-wait timeout, a failed swap, sc.exe refusing to start the
// service — left absolutely nothing to explain it afterward.
void Log(string level, string message)
{
    var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
    (level == "ERROR" ? Console.Error : Console.Out).WriteLine(line);

    try
    {
        Directory.CreateDirectory(logDir);
        var path = Path.Combine(logDir, $"updater-{DateTime.Now:yyyyMMdd}.log");
        File.AppendAllText(path, line + Environment.NewLine);
    }
    catch
    {
        // Best-effort — an unwritable log directory must never stop the actual update work this
        // process exists to do. The Console line above still carries the message if anyone is
        // watching live; this is only the durable copy for after the fact.
    }
}
