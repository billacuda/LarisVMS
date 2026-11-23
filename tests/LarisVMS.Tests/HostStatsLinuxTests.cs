using LarisVMS.Node;

namespace LarisVMS.Tests;

/// <summary>A Linux node's dashboard figures come from /proc; these pin the parsing to real file
/// shapes, and check DiskSpace answers on whatever OS runs the tests (the storage watermark and
/// archiving stop working if it returns null).</summary>
public sealed class HostStatsLinuxTests
{
    private const string ProcStat =
        "cpu  4705 150 1120 16250 520 0 30 0 0 0\n" +
        "cpu0 2352 75 560 8125 260 0 15 0 0 0\n" +
        "intr 1234 0 0\n";

    [Fact]
    public void ParsesTheAggregateCpuLine()
    {
        var times = HostStatsSampler.ParseProcStat(ProcStat);

        Assert.NotNull(times);
        // idle + iowait
        Assert.Equal(16250UL + 520, times.Value.Idle);
        // user + nice
        Assert.Equal(4705UL + 150, times.Value.User);
        // idle + iowait + system + irq + softirq + steal
        Assert.Equal(16250UL + 520 + 1120 + 0 + 30 + 0, times.Value.Kernel);
    }

    [Fact]
    public void CpuPercentFromTwoProcStatSamples()
    {
        var first = HostStatsSampler.ParseProcStat("cpu  100 0 100 800 0 0 0 0\n")!.Value;
        var second = HostStatsSampler.ParseProcStat("cpu  200 0 200 1400 0 0 0 0\n")!.Value;

        // 200 busy ticks out of 800 elapsed.
        Assert.Equal(25.0, HostStatsSampler.CpuPercentBetween(first, second));
    }

    [Fact]
    public void RejectsAProcStatWithoutACpuLine() => Assert.Null(HostStatsSampler.ParseProcStat("intr 1 2 3\n"));

    [Fact]
    public void ParsesMemInfo()
    {
        const string meminfo =
            "MemTotal:       16303428 kB\n" +
            "MemFree:          811892 kB\n" +
            "MemAvailable:    9876544 kB\n" +
            "Buffers:          123456 kB\n";

        var memory = HostStatsSampler.ParseMemInfo(meminfo);

        Assert.Equal((16303428L - 9876544L) * 1024, memory!.Value.Used);
        Assert.Equal(16303428L * 1024, memory.Value.Total);
    }

    [Fact]
    public void MemInfoWithoutMemAvailableGivesNothing() =>
        Assert.Null(HostStatsSampler.ParseMemInfo("MemTotal: 100 kB\n"));

    [Fact]
    public void DiskSpaceReportsTheTempFolder()
    {
        var usage = DiskSpace.TryGetUsage(Path.GetTempPath());

        Assert.NotNull(usage);
        Assert.True(usage.Value.TotalBytes > 0);
        Assert.InRange(usage.Value.FreeBytes, 0, usage.Value.TotalBytes);
    }

    [Fact]
    public void DiskSpaceIsNullForAMissingFolder() =>
        Assert.Null(DiskSpace.TryGetUsage(Path.Combine(Path.GetTempPath(), "larisvms-missing-" + Guid.NewGuid().ToString("N"))));
}
