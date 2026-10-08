namespace LarisVMS.Core.Update;

/// <summary>
/// Swaps a service's own executable for a staged update while it is still running. Windows won't let
/// a running image be overwritten or deleted, but it will let it be renamed: the running exe is moved
/// aside to "{name}.old" and the staged one moved into its place, so the next start of the service runs
/// the new build. The updater then only has to restart the service (its --restart-only mode).
///
/// This replaced handing the swap to LarisVMS.NodeUpdater after the service stopped. The SCM reports a
/// service Stopped before its process has fully exited, so the updater's copy could hit "Access to the
/// path is denied" on the still-locked exe, and it then left the service stopped. The updater binary is
/// also never replaced by auto-update (only the service exe is), so a fix there would only reach
/// machines that are reinstalled; doing the swap from the service itself ships with the update.
/// </summary>
public static class InPlaceBinarySwap
{
    public const string OldSuffix = ".old";

    /// <summary>Moves <paramref name="current"/> to "{current}.old" and <paramref name="staged"/> to
    /// <paramref name="current"/>. If the second move fails, the first is undone so the service is
    /// never left without its exe. Returns false (with the reason) instead of throwing.</summary>
    public static bool TrySwap(string staged, string current, out string? error)
    {
        var old = current + OldSuffix;
        try
        {
            // Left by the previous update; that process has exited, so it is no longer locked.
            if (File.Exists(old)) File.Delete(old);
            if (File.Exists(current)) File.Move(current, old);
            try
            {
                File.Move(staged, current);
            }
            catch
            {
                if (!File.Exists(current) && File.Exists(old)) File.Move(old, current);
                throw;
            }
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Deletes the "*.old" files a previous in-place swap left in <paramref name="directory"/>.
    /// Best-effort: run at startup, when the process that had them mapped is gone.</summary>
    public static void CleanUp(string directory)
    {
        try
        {
            foreach (var old in Directory.EnumerateFiles(directory, "*.exe" + OldSuffix))
            {
                try { File.Delete(old); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* retried next start */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* unreadable install dir: nothing to clean */ }
    }
}
