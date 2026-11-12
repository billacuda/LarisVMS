using System.Security.AccessControl;
using System.Security.Principal;
using LarisVMS.Core.Security;

namespace LarisVMS.Tests;

/// <summary>
/// The data folder keeps only trusted accounts: a grant someone added for another account (the way a
/// local user who created the folder first, or added themselves, would) is removed, not just the broad
/// groups, while a Windows service's own virtual account keeps its grant.
/// </summary>
public class LocalDataAclTests
{
    private static readonly SecurityIdentifier Guests = new(WellKnownSidType.BuiltinGuestsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    // NT SERVICE\TrustedInstaller: a service virtual account present on every Windows machine.
    private static readonly SecurityIdentifier ServiceAccount = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    private static HashSet<SecurityIdentifier> Allowed(DirectoryInfo dir) =>
        dir.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow).Select(r => (SecurityIdentifier)r.IdentityReference).ToHashSet();

    private static DirectoryInfo NewFolderWithExtraGrants()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "larisvms-acl-" + Guid.NewGuid().ToString("N")));
        var security = dir.GetAccessControl();
        foreach (var sid in new[] { Guests, Users, ServiceAccount })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(security);
        return dir;
    }

    [Fact]
    public void TheTreeKeepsOnlyTrustedAccountsAndServiceAccounts()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = NewFolderWithExtraGrants();
        try
        {
            Assert.Null(LocalDataAcl.SecureTree(dir.FullName));

            var allowed = Allowed(dir);
            Assert.DoesNotContain(Guests, allowed);
            Assert.DoesNotContain(Users, allowed);
            Assert.Contains(ServiceAccount, allowed);
            Assert.Contains(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), allowed);
            Assert.Contains(WindowsIdentity.GetCurrent().User!, allowed);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ASecretsFolderKeepsOnlyTrustedAccounts()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = NewFolderWithExtraGrants();
        try
        {
            Assert.Null(LocalDataAcl.SecureFolder(dir.FullName));

            var allowed = Allowed(dir);
            Assert.DoesNotContain(Guests, allowed);
            Assert.DoesNotContain(Users, allowed);
            Assert.True(dir.GetAccessControl().AreAccessRulesProtected);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
