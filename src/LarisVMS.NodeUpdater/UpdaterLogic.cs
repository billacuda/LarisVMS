namespace LarisVMS.NodeUpdater;

/// <summary>
/// The pure, directly-testable pieces of the binary-swap helper — argument parsing and the
/// backup/move/cleanup file logic — split out from Program.cs's top-level statements (which have no
/// callable surface for a test to invoke) the same way NodeWorker.ShouldDiscardSegment is pulled out
/// of NodeWorker for the same reason. The sc.exe-dependent pieces (polling the service for Stopped,
/// starting it back up, re-applying failure recovery) stay inline in Program.cs, ported as-is from
/// dploid.AgentUpdater/Program.cs — this environment has no real Windows Service to poll, so there's
/// nothing meaningful to unit test there without a fake process host.
/// </summary>
internal static class UpdaterLogic
{
    public const string DefaultServiceName = "LarisVMSNode";

    /// <summary>RestartOnly skips the binary swap entirely and only performs the wait-for-stopped /
    /// start-again half — what the Nodes page's own "Restart service" button needs. Reusing this
    /// binary rather than having the node shell out to sc.exe itself keeps one implementation of
    /// "outlive the service, then bring it back", which is the part that has to run *outside* the
    /// process being restarted.
    ///
    /// NewVisionBinary/CurrentVisionBinary (object detection plan follow-up) are both null together
    /// when this update carries no Vision Service binary (UpdateService only ever passes both or
    /// neither) — a second, optional, best-effort swap alongside the required Node one. Program.cs
    /// never lets a Vision swap failure abort the Node swap or leave the service un-restarted, the
    /// same "additive, never blocks recording" philosophy the rest of this feature already follows.</summary>
    public record ParsedArgs(string? NewBinary, string? CurrentBinary, string ServiceName, bool RestartOnly,
        string? NewVisionBinary = null, string? CurrentVisionBinary = null);

    /// <summary>Same "--flag value" scanning dploid.AgentUpdater's Program.cs uses inline — pulled out
    /// here only so it has something a test can call directly.</summary>
    public static ParsedArgs ParseArgs(string[] args)
    {
        string? newBinary = null, currentBinary = null, newVisionBinary = null, currentVisionBinary = null;
        var serviceName = DefaultServiceName;
        var restartOnly = false;

        // Scans to args.Length (not args.Length - 1) so a valueless flag in the final position is
        // still seen — the paired "--flag value" cases below read args[++i] only after confirming a
        // next element exists, which is what the old bound was protecting.
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--new" when i + 1 < args.Length: newBinary = args[++i]; break;
                case "--current" when i + 1 < args.Length: currentBinary = args[++i]; break;
                case "--new-vision" when i + 1 < args.Length: newVisionBinary = args[++i]; break;
                case "--current-vision" when i + 1 < args.Length: currentVisionBinary = args[++i]; break;
                case "--service" when i + 1 < args.Length: serviceName = args[++i]; break;
                case "--restart-only": restartOnly = true; break;
            }
        }

        return new ParsedArgs(newBinary, currentBinary, serviceName, restartOnly, newVisionBinary, currentVisionBinary);
    }

    /// <summary>Backs up currentBinary to "{currentBinary}.bak" (if it exists), moves newBinary over
    /// currentBinary, then deletes the backup on success. On failure the backup (if one was made) is
    /// deliberately left in place rather than auto-restored — Program.cs surfaces the error and exits
    /// non-zero; an operator can restore .bak by hand, which is safer than this code guessing at a
    /// recovery action mid-failure. Returns true on success.</summary>
    public static bool TrySwapBinary(string newBinary, string currentBinary, out string? error)
    {
        var backupPath = currentBinary + ".bak";

        try
        {
            if (File.Exists(currentBinary))
                File.Copy(currentBinary, backupPath, overwrite: true);

            File.Move(newBinary, currentBinary, overwrite: true);

            try { File.Delete(backupPath); }
            catch { /* best effort — a lingering .bak next to a successfully-swapped binary is harmless */ }

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
