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
//   2. Back up the current binary, move the new one over it (UpdaterLogic.TrySwapBinary).
//   3. Start the service again and re-apply the same failure-recovery config install-node.ps1 sets at
//      install time — a raw binary swap doesn't touch service configuration, but re-applying here is
//      cheap insurance against a service that was ever re-registered without it.

using System.Diagnostics;
using LarisVMS.NodeUpdater;

const int MaxWaitSeconds = 60;

var parsed = UpdaterLogic.ParseArgs(args);

// --restart-only: no binary swap, just outlive the service and bring it back (the Nodes page's own
// "Restart service" button). Everything below is written as "wait for stopped → [swap] → start", so
// this mode only skips the middle step; the two halves that matter are shared verbatim.
if (!parsed.RestartOnly)
{
    if (parsed.NewBinary is null || parsed.CurrentBinary is null)
    {
        Console.Error.WriteLine("Usage: LarisVMS.NodeUpdater.exe --new <path> --current <path> [--service <name>]");
        Console.Error.WriteLine("   or: LarisVMS.NodeUpdater.exe --restart-only [--service <name>]");
        return 1;
    }

    if (!File.Exists(parsed.NewBinary))
    {
        Console.Error.WriteLine($"Staged binary not found: {parsed.NewBinary}");
        return 1;
    }
}

// ── Wait for service to stop ───────────────────────────────────────────────

Console.WriteLine($"Waiting for service '{parsed.ServiceName}' to stop...");

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
    Console.Error.WriteLine($"Service '{parsed.ServiceName}' did not stop within {MaxWaitSeconds}s. Aborting.");
    return 1;
}

Console.WriteLine("Service stopped.");

// ── Swap binary ────────────────────────────────────────────────────────────

if (parsed.RestartOnly)
{
    Console.WriteLine("Restart-only mode — skipping binary swap.");
}
else if (!UpdaterLogic.TrySwapBinary(parsed.NewBinary!, parsed.CurrentBinary!, out var swapError))
{
    Console.Error.WriteLine($"Failed to swap binary: {swapError}");
    return 1;
}
else
{
    Console.WriteLine($"Binary swapped: {parsed.NewBinary} -> {parsed.CurrentBinary}");
}

// ── Start service ──────────────────────────────────────────────────────────

Console.WriteLine($"Starting service '{parsed.ServiceName}'...");
try
{
    RunCommand("sc.exe", $"start {parsed.ServiceName}");
    Console.WriteLine("Service started.");

    // Re-apply failure recovery in case the service was re-registered — same config
    // install-node.ps1 sets at install time (see that script's own "configure service recovery" step).
    try { RunCommand("sc.exe", $"failure {parsed.ServiceName} reset= 86400 actions= restart/60000/restart/60000/restart/60000"); }
    catch { /* best-effort; not fatal */ }
    try { RunCommand("sc.exe", $"failureflag {parsed.ServiceName} 1"); }
    catch { /* best-effort; not fatal */ }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to start service: {ex.Message}");
    return 1;
}

return 0;

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
