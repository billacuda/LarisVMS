using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace LarisVMS.Core.Security;

/// <summary>
/// Locks down LarisVMS's machine-wide data. %ProgramData% lets every local user read and create
/// files, and LarisVMS keeps secrets there (the data-protection key ring, node.config, certificates,
/// the update staging folder), so each service, at startup, limits <see cref="Root"/> to SYSTEM,
/// Administrators, its own account, and the accounts the other LarisVMS services run as. Nothing else
/// is kept: a local user who created the folder before LarisVMS was installed, or added themselves
/// to it, loses that access, and the folder's owner is set to Administrators. (When a domain service
/// account can't be looked up right then, grants to domain accounts are left in place until it can;
/// local accounts never are.) No-op off Windows.
/// </summary>
public static class LocalDataAcl
{
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LarisVMS");

    /// <summary>Protects <see cref="Root"/> (creating it if needed) and resets its contents to inherit
    /// from it. Returns why it couldn't, or null.</summary>
    public static string? SecureRoot() => SecureTree(Root);

    /// <summary><see cref="SecureRoot"/> for another folder (tests use a temporary one).</summary>
    public static string? SecureTree(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            ProtectDirectoryTree(path);
            return null;
        }
        catch (Exception ex) when (IsAclFailure(ex))
        {
            return ex.Message;
        }
    }

    /// <summary>Limits one folder (small, holding secrets, such as the data-protection key ring) to the
    /// trusted accounts right away, with its contents inheriting from it. For use before the folder's
    /// contents are first written, while the rest of <see cref="Root"/> is secured in the background.
    /// Returns why it couldn't, or null.</summary>
    public static string? SecureFolder(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var dir = new DirectoryInfo(path);
            if (!dir.Exists) dir.Create();
            var (trusted, _) = TrustedAccounts();
            if (!IsLockedDown(dir, trusted, keepForeignDomain: false))
            {
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                foreach (var sid in trusted) security.AddAccessRule(FullControl(sid, inheritable: true));
                dir.SetAccessControl(security);
                SetTrustedOwner(dir, trusted);
            }
            foreach (var inner in dir.EnumerateFileSystemInfos("*", SearchOption.AllDirectories)) TryResetToInherited(inner);
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
        var (trusted, someUnresolved) = TrustedAccounts();

        // Rewriting the root makes Windows re-propagate inheritance through everything under it, which
        // is slow with recordings there, so it only happens when the root isn't already locked down.
        if (!IsLockedDown(dir, trusted, keepForeignDomain: someUnresolved))
        {
            var current = dir.GetAccessControl();
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in trusted) security.AddAccessRule(FullControl(sid, inheritable: true));
            // A service account that couldn't be looked up just now keeps its grant (a domain account),
            // so a domain controller being unreachable doesn't lock a service out.
            foreach (var rule in AllowRules(current).Where(r => !trusted.Contains(r.Sid)
                         && (IsVirtualServiceAccount(r.Sid) || (someUnresolved && IsDomainAccount(r.Sid)))).DistinctBy(r => r.Sid))
                security.AddAccessRule(FullControl(rule.Sid, inheritable: true));
            dir.SetAccessControl(security);
            SetTrustedOwner(dir, trusted);
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

    /// <summary>Only trusted accounts are allowed in (plus, while a service account can't be looked up,
    /// domain accounts), every trusted account has full control, and a trusted account owns it.</summary>
    [SupportedOSPlatform("windows")]
    private static bool IsLockedDown(DirectoryInfo dir, IReadOnlySet<SecurityIdentifier> trusted, bool keepForeignDomain)
    {
        var security = dir.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!security.AreAccessRulesProtected) return false;
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !trusted.Contains(owner)) return false;
        var rules = AllowRules(security);
        if (rules.Any(r => !trusted.Contains(r.Sid) && !IsVirtualServiceAccount(r.Sid) && !(keepForeignDomain && IsDomainAccount(r.Sid)))) return false;
        return trusted.All(sid => rules.Any(r => r.Sid.Equals(sid) && r.Rights.HasFlag(FileSystemRights.FullControl)));
    }

    [SupportedOSPlatform("windows")]
    private static List<(SecurityIdentifier Sid, FileSystemRights Rights)> AllowRules(DirectorySecurity security) =>
        security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Where(r => r.AccessControlType == AccessControlType.Allow)
            .Select(r => ((SecurityIdentifier)r.IdentityReference, r.FileSystemRights)).ToList();

    /// <summary>A Windows service's own virtual account (NT SERVICE\…), for a LarisVMS service an admin
    /// set to run as one. No one can sign in as one, so its grant stays.</summary>
    [SupportedOSPlatform("windows")]
    internal static bool IsVirtualServiceAccount(SecurityIdentifier sid) =>
        sid.Value.StartsWith("S-1-5-80-", StringComparison.Ordinal);

    /// <summary>An account from a domain, not this machine's own accounts or a built-in group.</summary>
    [SupportedOSPlatform("windows")]
    private static bool IsDomainAccount(SecurityIdentifier sid)
    {
        if (sid.AccountDomainSid is null) return false; // built-in groups and well-known accounts
        try
        {
            var name = sid.Translate(typeof(NTAccount)).Value;
            var slash = name.IndexOf('\\');
            return slash > 0 && !name[..slash].Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            return true; // can't be looked up locally, so not one of this machine's accounts
        }
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

    /// <summary>Makes Administrators the owner (the owner can always change the permissions, so a
    /// local user who created the folder mustn't stay its owner). Throws when the owner can't be made
    /// a trusted account, so the caller reports the folder as not secured.</summary>
    [SupportedOSPlatform("windows")]
    private static void SetTrustedOwner(DirectoryInfo dir, IReadOnlySet<SecurityIdentifier> trusted)
    {
        try
        {
            var security = dir.GetAccessControl(AccessControlSections.Owner);
            security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            dir.SetAccessControl(security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException) { /* checked below */ }
        if (dir.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
            || !trusted.Contains(owner))
            throw new InvalidOperationException($"{dir.FullName} is owned by an account LarisVMS doesn't trust, and its owner couldn't be changed.");
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<SecurityIdentifier> Trusted() => TrustedAccounts().Sids;

    /// <summary>SYSTEM, Administrators, this process's account, and the LarisVMS service accounts; and
    /// whether any service account couldn't be looked up.</summary>
    [SupportedOSPlatform("windows")]
    private static (HashSet<SecurityIdentifier> Sids, bool SomeUnresolved) TrustedAccounts()
    {
        var sids = new HashSet<SecurityIdentifier>
        {
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
        };
        if (WindowsIdentity.GetCurrent().User is { } self) sids.Add(self);
        // Every LarisVMS service on this machine shares the folder, possibly under different accounts
        // (the web server as a domain account, the node as LocalSystem): whichever locks it down first
        // must not lock the others out.
        var (accounts, unresolved) = ServiceAccounts();
        sids.UnionWith(accounts);
        return (sids, unresolved);
    }

    private static readonly string[] ServiceNames = ["LarisVMSWeb", "LarisVMSNode", "LarisVMSProxy"];

    /// <summary>The accounts the installed LarisVMS services run as (from their service entries),
    /// skipping ones that can't be resolved.</summary>
    [SupportedOSPlatform("windows")]
    private static (List<SecurityIdentifier> Sids, bool SomeUnresolved) ServiceAccounts()
    {
        var sids = new List<SecurityIdentifier>();
        var unresolved = false;
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
                // skipped; grants to domain accounts stay until it can be (see ProtectDirectoryTree).
                unresolved = true;
            }
        }
        return (sids, unresolved);
    }

    [SupportedOSPlatform("windows")]
    private static FileSystemAccessRule FullControl(SecurityIdentifier sid, bool inheritable) => new(sid, FileSystemRights.FullControl,
        inheritable ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None,
        PropagationFlags.None, AccessControlType.Allow);
}
