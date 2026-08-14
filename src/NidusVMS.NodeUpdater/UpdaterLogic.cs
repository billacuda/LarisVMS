namespace NidusVMS.NodeUpdater;

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
    public const string DefaultServiceName = "NidusVMSNode";

    public record ParsedArgs(string? NewBinary, string? CurrentBinary, string ServiceName);

    /// <summary>Same "--flag value" scanning dploid.AgentUpdater's Program.cs uses inline — pulled out
    /// here only so it has something a test can call directly.</summary>
    public static ParsedArgs ParseArgs(string[] args)
    {
        string? newBinary = null, currentBinary = null;
        var serviceName = DefaultServiceName;

        for (var i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--new": newBinary = args[++i]; break;
                case "--current": currentBinary = args[++i]; break;
                case "--service": serviceName = args[++i]; break;
            }
        }

        return new ParsedArgs(newBinary, currentBinary, serviceName);
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
