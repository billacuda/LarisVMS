using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>Covers BackupService's pure helpers — ValidateDirectory and IsLocalServer — the
/// arithmetic/validation isolated from the actual BACKUP DATABASE I/O, same "isolate the pure part"
/// shape as NodeServiceTests/TimelineServiceTests.</summary>
public class BackupServiceTests
{
    [Fact]
    public void ValidateDirectoryRejectsAMissingDirectory()
    {
        Assert.Contains("No backup directory", BackupService.ValidateDirectory(null));
        Assert.Contains("No backup directory", BackupService.ValidateDirectory("  "));
    }

    [Fact]
    public void ValidateDirectoryRejectsARelativePath()
    {
        Assert.Contains("absolute path", BackupService.ValidateDirectory(@"Backups\LarisVMS"));
    }

    [Fact]
    public void ValidateDirectoryRejectsCharactersThatWouldBreakTheUnparameterizedBackupSql()
    {
        Assert.Contains("invalid characters", BackupService.ValidateDirectory(@"D:\Backups\it's mine"));
        Assert.Contains("invalid characters", BackupService.ValidateDirectory(@"D:\Backups\[test]"));
    }

    [Fact]
    public void ValidateDirectoryAcceptsAnOrdinaryAbsolutePath()
    {
        Assert.Null(BackupService.ValidateDirectory(@"D:\Backups\LarisVMS"));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("(local)")]
    [InlineData("localhost")]
    [InlineData("")]
    public void IsLocalServerRecognizesEveryLocalAlias(string dataSource)
    {
        Assert.True(BackupService.IsLocalServer(dataSource));
    }

    [Fact]
    public void IsLocalServerRecognizesThisMachinesOwnName()
    {
        Assert.True(BackupService.IsLocalServer(Environment.MachineName));
    }

    [Fact]
    public void IsLocalServerRecognizesANamedInstanceOnALocalAlias()
    {
        Assert.True(BackupService.IsLocalServer(@".\SQLEXPRESS"));
    }

    [Fact]
    public void IsLocalServerRejectsARemoteHost()
    {
        Assert.False(BackupService.IsLocalServer("sql1.internal.example.com"));
    }
}
