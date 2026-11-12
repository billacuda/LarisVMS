using LarisVMS.Media;

namespace LarisVMS.Tests;

/// <summary>ffmpeg runs as SYSTEM, so one found on PATH in a folder ordinary users can write to is
/// passed over.</summary>
public class FfmpegPathSafetyTests
{
    [Fact]
    public void SystemFoldersArentWritableByUsersButProgramDataIs()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.False(FfmpegPathResolver.WritableByNonAdmins(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.True(FfmpegPathResolver.WritableByNonAdmins(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)));
    }
}
