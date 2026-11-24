using System.Security.Cryptography;
using System.Text;
using LarisVMS.Core.Security;

namespace LarisVMS.Tests;

/// <summary>
/// MachineSecret protects node.config, proxy.config, and the Web server's database password. The
/// Linux format (AES-256-GCM keyed off machine-id) is exercised directly with a given machine ID so it
/// runs on any OS; the public round trip uses whatever this OS does (DPAPI on Windows).
/// </summary>
public sealed class MachineSecretTests
{
    private static readonly byte[] MachineA = "0123456789abcdef0123456789abcdef"u8.ToArray();
    private static readonly byte[] MachineB = "fedcba9876543210fedcba9876543210"u8.ToArray();
    private static readonly byte[] Entropy = "LarisVMS.Test"u8.ToArray();

    [Fact]
    public void TheDataFoldersMachineIdComesFirst()
    {
        // The Docker images keep their machine ID in the data volume, so a recreated container can
        // still read its secrets; on a normal install that file doesn't exist and /etc/machine-id is used.
        var paths = MachineSecret.MachineIdPaths;
        Assert.Equal(Path.Combine(LarisVMS.Core.LarisPaths.DataRoot, "machine-id"), paths[0]);
        Assert.Contains("/etc/machine-id", paths);
    }

    [Fact]
    public void RoundTripsOnThisMachine()
    {
        var data = Encoding.UTF8.GetBytes("{\"secret\":\"x\"}");
        Assert.Equal(data, MachineSecret.Unprotect(MachineSecret.Protect(data, Entropy), Entropy));
        Assert.Equal("p@ss", MachineSecret.UnprotectString(MachineSecret.ProtectString("p@ss", null), null));
    }

    [Fact]
    public void LinuxFormatRoundTripsAndDoesNotContainThePlaintext()
    {
        var data = Encoding.UTF8.GetBytes("registration-secret");
        var sealedBytes = MachineSecret.ProtectWithMachineId(data, Entropy, MachineA);

        Assert.Equal(data, MachineSecret.UnprotectWithMachineId(sealedBytes, Entropy, MachineA));
        Assert.DoesNotContain("registration-secret", Encoding.UTF8.GetString(sealedBytes));
    }

    [Fact]
    public void LinuxFormatUsesAFreshNonceEachTime()
    {
        var data = Encoding.UTF8.GetBytes("same");
        Assert.NotEqual(
            MachineSecret.ProtectWithMachineId(data, Entropy, MachineA),
            MachineSecret.ProtectWithMachineId(data, Entropy, MachineA));
    }

    [Fact]
    public void LinuxFormatCantBeReadOnAnotherMachine()
    {
        var sealedBytes = MachineSecret.ProtectWithMachineId("x"u8.ToArray(), Entropy, MachineA);
        Assert.ThrowsAny<CryptographicException>(() => MachineSecret.UnprotectWithMachineId(sealedBytes, Entropy, MachineB));
    }

    [Fact]
    public void LinuxFormatCantBeReadWithOtherEntropy()
    {
        var sealedBytes = MachineSecret.ProtectWithMachineId("x"u8.ToArray(), Entropy, MachineA);
        Assert.ThrowsAny<CryptographicException>(() => MachineSecret.UnprotectWithMachineId(sealedBytes, null, MachineA));
    }

    [Fact]
    public void LinuxFormatRejectsTamperingAndForeignData()
    {
        var sealedBytes = MachineSecret.ProtectWithMachineId("x"u8.ToArray(), Entropy, MachineA);
        sealedBytes[^1] ^= 0xFF;
        Assert.ThrowsAny<CryptographicException>(() => MachineSecret.UnprotectWithMachineId(sealedBytes, Entropy, MachineA));
        Assert.ThrowsAny<CryptographicException>(() => MachineSecret.UnprotectWithMachineId("{}"u8.ToArray(), Entropy, MachineA));
    }

    [Fact]
    public void WriteFileReplacesTheFileAndIsOwnerOnlyOffWindows()
    {
        var dir = Path.Combine(Path.GetTempPath(), "larisvms-ms-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "node.config");
            MachineSecret.WriteFile(path, [1, 2, 3]);
            MachineSecret.WriteFile(path, [4, 5]);

            Assert.Equal(new byte[] { 4, 5 }, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".tmp"));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
