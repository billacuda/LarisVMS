using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace LarisVMS.Core.Security;

/// <summary>
/// Locks down LarisVMS's machine-wide data. %ProgramData% lets every local user read and create
/// files, and LarisVMS keeps secrets there (the data-protection key ring, node.config, certificates,
/// the update staging folder), so each service, at startup, limits <see cref="Root"/> to SYSTEM,
/// Administrators, and its own account. Other accounts already granted explicitly (another LarisVMS
/// service on the same machine under a different account) keep their access; only the broad grants
/// — Users, Everyone, Authenticated Users, Interactive, CREATOR OWNER — are removed. No-op off Windows.
/// </summary>
public static class LocalDataAcl
{
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LarisVMS");

    /// <summary>Protects <see cref="Root"/> (creating it if needed) and resets its contents to inherit
    /// from it. Returns why it couldn't, or null.</summary>
    public static string? SecureRoot()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            ProtectDirectoryTree(Root);
            return null;
        }
        catch (Exception ex) when (IsAclFailure(ex))
        {
            return ex.Message;
        }
    }

    /// <summary>Limits one file to SYSTEM, Administrators, and this service (e.g. a config file holding
    /// a password next to the program). Returns why it couldn't, or null.</summary>
    public static string? ProtectFile(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return null;
        try
        {
            ProtectFileCore(path);
            return null;
        }
        catch (Exception ex) when (IsAclFailure(ex))
        {
            return ex.Message;
        }
    }

    /// <summary>Deletes <paramref name="path"/> and creates it again, empty, with only SYSTEM,
    /// Administrators, and this service allowed — for a staging folder whose contents are about to be
    /// trusted, so nothing planted beforehand (or its owner) survives.</summary>
    public static void RecreateProtectedDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in Trusted()) security.AddAccessRule(FullControl(sid, inheritable: true));
        new DirectoryInfo(path).Create(security);
    }

    private static bool IsAclFailure(Exception ex) =>
        ex is UnauthorizedAccessException or IOException or PrivilegeNotHeldException or InvalidOperationException or ArgumentException;

    /// <summary>Small folders holding secrets or files that get trusted later; their whole contents
    /// are reset. Everything else (recordings can live under the root) only has its top level checked.</summary>
    private static readonly string[] SensitiveFolders = ["keys", "update", "vision-native"];

    [SupportedOSPlatform("windows")]
    private static void ProtectDirectoryTree(string path)
    {
        var dir = new DirectoryInfo(path);
        if (!dir.Exists) dir.Create();

        // Rewriting the root makes Windows re-propagate inheritance through everything under it, which
        // is slow with recordings there, so it only happens when the root isn't already locked down.
        if (!IsLockedDown(dir.GetAccessControl()))
        {
            var security = dir.GetAccessControl();
            // Keep what's inherited today as explicit entries, then drop the broad ones from that.
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
            dir.SetAccessControl(security);
            security = dir.GetAccessControl();
            foreach (var sid in Broad()) security.PurgeAccessRules(sid);
            foreach (var sid in Trusted()) security.AddAccessRule(FullControl(sid, inheritable: true));
            dir.SetAccessControl(security);
            TrySetOwner(dir);
        }

        // Items a local user created before the lockdown may carry their own grants; they go back to
        // inheriting from the root.
        foreach (var entry in dir.EnumerateFileSystemInfos())
        {
            TryResetToInherited(entry);
            if (entry is DirectoryInfo sub && SensitiveFolders.Contains(sub.Name, StringComparer.OrdinalIgnoreCase))
                foreach (var inner in sub.EnumerateFileSystemInfos("*", SearchOption.AllDirectories)) TryResetToInherited(inner);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsLockedDown(DirectorySecurity security)
    {
        if (!security.AreAccessRulesProtected) return false;
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Where(r => r.AccessControlType == AccessControlType.Allow).ToList();
        var broad = Broad().ToHashSet();
        if (rules.Any(r => broad.Contains((SecurityIdentifier)r.IdentityReference))) return false;
        return Trusted().All(sid => rules.Any(r => sid.Equals(r.IdentityReference)
            && r.FileSystemRights.HasFlag(FileSystemRights.FullControl)));
    }

    [SupportedOSPlatform("windows")]
    private static void TryResetToInherited(FileSystemInfo entry)
    {
        try
        {
            if (entry is DirectoryInfo d) ResetToInherited(d.GetAccessControl(), s => d.SetAccessControl(s));
            else if (entry is FileInfo f) ResetToInherited(f.GetAccessControl(), s => f.SetAccessControl(s));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException) { /* best effort per item */ }
    }

    /// <summary>Drops an item's own grants so it only inherits. Leaves an item that already only
    /// inherits untouched (no write, no re-propagation).</summary>
    [SupportedOSPlatform("windows")]
    private static void ResetToInherited<T>(T security, Action<T> apply) where T : FileSystemSecurity
    {
        var explicitRules = security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToList();
        if (!security.AreAccessRulesProtected && explicitRules.Count == 0) return;
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        foreach (var rule in explicitRules) security.RemoveAccessRuleSpecific(rule);
        apply(security);
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectFileCore(string path)
    {
        var file = new FileInfo(path);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in Trusted()) security.AddAccessRule(FullControl(sid, inheritable: false));
        file.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void TrySetOwner(DirectoryInfo dir)
    {
        try
        {
            var security = dir.GetAccessControl(AccessControlSections.Owner);
            security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            dir.SetAccessControl(security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException) { /* not every service account may */ }
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<SecurityIdentifier> Trusted()
    {
        yield return new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        yield return new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        if (WindowsIdentity.GetCurrent().User is { } self) yield return self;
        // Every LarisVMS service on this machine shares the folder, possibly under different accounts
        // (the web server as a domain account, the node as LocalSystem): whichever locks it down first
        // must not lock the others out.
        foreach (var sid in ServiceAccounts()) yield return sid;
    }

    private static readonly string[] ServiceNames = ["LarisVMSWeb", "LarisVMSNode", "LarisVMSProxy"];

    /// <summary>The accounts the installed LarisVMS services run as (from their service entries),
    /// skipping ones that can't be resolved.</summary>
    [SupportedOSPlatform("windows")]
    private static List<SecurityIdentifier> ServiceAccounts()
    {
        var sids = new List<SecurityIdentifier>();
        foreach (var name in ServiceNames)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
                if (key?.GetValue("ObjectName") is not string account || string.IsNullOrWhiteSpace(account)) continue;
                if (account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)) continue; // SYSTEM is already in
                if (account.StartsWith(@".\", StringComparison.Ordinal)) account = Environment.MachineName + account[1..];
                sids.Add((SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier)));
            }
            catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
            {
                // An account that can't be resolved right now (domain controller unreachable) is
                // skipped; its existing explicit grant, if any, stays.
            }
        }
        return sids;
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<SecurityIdentifier> Broad()
    {
        yield return new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        yield return new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        yield return new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        yield return new SecurityIdentifier(WellKnownSidType.InteractiveSid, null);
        yield return new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null);
    }

    [SupportedOSPlatform("windows")]
    private static FileSystemAccessRule FullControl(SecurityIdentifier sid, bool inheritable) => new(sid, FileSystemRights.FullControl,
        inheritable ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None,
        PropagationFlags.None, AccessControlType.Allow);
}
