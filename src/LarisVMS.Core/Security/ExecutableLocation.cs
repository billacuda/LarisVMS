using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace LarisVMS.Core.Security;

/// <summary>
/// Whether a program LarisVMS starts from a configured or discovered path is safe to run with the
/// service's rights: on this machine (not a network share), in a folder ordinary users can't write
/// to, so nobody can swap it for something else.
/// </summary>
public static class ExecutableLocation
{
    /// <summary>A local, rooted path (not \\server\share or a mapped network folder) whose folder
    /// ordinary users can't write to.</summary>
    public static bool IsTrusted(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return false;
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return false;
        if (Uri.TryCreate(Path.GetFullPath(path), UriKind.Absolute, out var uri) && uri.IsUnc) return false;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        return dir is not null && !WritableByNonAdmins(dir);
    }

    /// <summary>The first <paramref name="fileName"/> on PATH in a trusted folder, or null.</summary>
    public static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate) && IsTrusted(candidate)) return candidate;
            }
            catch { /* malformed PATH entry — skip it */ }
        }
        return null;
    }

    /// <summary>True when Users, Authenticated Users, Everyone, or Interactive may create or change
    /// files in <paramref name="dir"/>, or when that can't be told. Not Windows: false (the check
    /// doesn't apply).</summary>
    public static bool WritableByNonAdmins(string dir)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return HasBroadWrite(dir);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException
                                   or ArgumentException or NotSupportedException or SystemException)
        {
            return true; // can't tell: don't run it
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool HasBroadWrite(string dir)
    {
        var acl = new DirectoryInfo(dir).GetAccessControl();
        var broad = new[]
        {
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
        };
        const FileSystemRights write = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete
                                       | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & write) != 0
                && broad.Contains(rule.IdentityReference))
                return true;
        }
        return false;
    }
}
