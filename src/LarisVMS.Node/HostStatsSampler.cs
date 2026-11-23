using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using LarisVMS.Core.Dtos;

namespace LarisVMS.Node;

/// <summary>
/// The node machine's own load, sampled once per heartbeat for the dashboard: CPU percent busy across
/// all cores and network bytes/s across active adapters (both as deltas since the previous sample, so
/// the first call reports them as null), plus physical memory used/total. Windows reads these from
/// kernel32; Linux from /proc/stat and /proc/meminfo. Any other OS returns null and the dashboard
/// shows no host figures for the node.
/// </summary>
public sealed class HostStatsSampler
{
    private CpuTimes? _lastCpu;
    private (long Received, long Sent, long Timestamp)? _lastNet;

    public NodeHostStats? Sample()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return null;

        double? cpuPercent = null;
        if (TryGetCpuTimes() is { } cpu)
        {
            if (_lastCpu is { } previous) cpuPercent = CpuPercentBetween(previous, cpu);
            _lastCpu = cpu;
        }

        var (memoryUsed, memoryTotal) = TryGetMemory();

        long? receivePerSec = null, sendPerSec = null;
        if (TryGetNetworkBytes() is { } net)
        {
            var now = Stopwatch.GetTimestamp();
            if (_lastNet is { } previous)
            {
                var seconds = Stopwatch.GetElapsedTime(previous.Timestamp, now).TotalSeconds;
                receivePerSec = RatePerSecond(previous.Received, net.Received, seconds);
                sendPerSec = RatePerSecond(previous.Sent, net.Sent, seconds);
            }
            _lastNet = (net.Received, net.Sent, now);
        }

        return new NodeHostStats(cpuPercent, memoryUsed, memoryTotal, receivePerSec, sendPerSec);
    }

    /// <summary>Idle/kernel/user time in 100 ns units, as GetSystemTimes reports them — kernel time
    /// includes idle time.</summary>
    internal readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);

    internal static double? CpuPercentBetween(CpuTimes previous, CpuTimes current)
    {
        if (current.Idle < previous.Idle || current.Kernel < previous.Kernel || current.User < previous.User) return null;
        var idle = current.Idle - previous.Idle;
        var total = (current.Kernel - previous.Kernel) + (current.User - previous.User);
        if (total == 0) return null;
        var busy = total > idle ? total - idle : 0;
        return Math.Round(100.0 * busy / total, 1);
    }

    /// <summary>Null on a counter that went backwards (an adapter reset or removed between samples) or
    /// an interval too short to mean anything.</summary>
    internal static long? RatePerSecond(long previous, long current, double seconds)
    {
        if (seconds < 0.5 || current < previous) return null;
        return (long)Math.Round((current - previous) / seconds);
    }

    private static CpuTimes? TryGetCpuTimes()
    {
        if (!OperatingSystem.IsWindows()) return TryRead("/proc/stat") is { } stat ? ParseProcStat(stat) : null;
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        return new CpuTimes(idle.ToUInt64(), kernel.ToUInt64(), user.ToUInt64());
    }

    private static (long? Used, long? Total) TryGetMemory()
    {
        if (!OperatingSystem.IsWindows())
            return TryRead("/proc/meminfo") is { } meminfo && ParseMemInfo(meminfo) is { } m ? (m.Used, m.Total) : (null, null);
        var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref memory)) return (null, null);
        return ((long)(memory.TotalPhys - memory.AvailPhys), (long)memory.TotalPhys);
    }

    private static string? TryRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The aggregate "cpu" line of /proc/stat (user nice system idle iowait irq softirq steal,
    /// in clock ticks), mapped onto the Windows shape <see cref="CpuPercentBetween"/> expects: Kernel
    /// includes Idle, and waiting on I/O counts as idle.</summary>
    internal static CpuTimes? ParseProcStat(string stat)
    {
        var line = stat.Split('\n').FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
        if (line is null) return null;
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(f => ulong.TryParse(f, out var v) ? v : 0UL).ToArray();
        if (fields.Length < 4) return null;
        ulong At(int i) => i < fields.Length ? fields[i] : 0;
        var idle = At(3) + At(4);
        var system = At(2) + At(5) + At(6) + At(7);
        return new CpuTimes(idle, idle + system, At(0) + At(1));
    }

    /// <summary>Used and total physical memory, in bytes, from /proc/meminfo (MemTotal less
    /// MemAvailable, both in kB).</summary>
    internal static (long Used, long Total)? ParseMemInfo(string meminfo)
    {
        long? total = null, available = null;
        foreach (var line in meminfo.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !long.TryParse(parts[1], out var kb)) continue;
            if (parts[0] == "MemTotal:") total = kb * 1024;
            else if (parts[0] == "MemAvailable:") available = kb * 1024;
        }
        return total is { } t && available is { } a ? (t - a, t) : null;
    }

    private static (long Received, long Sent)? TryGetNetworkBytes()
    {
        try
        {
            long received = 0, sent = 0;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var stats = nic.GetIPStatistics();
                received += stats.BytesReceived;
                sent += stats.BytesSent;
            }
            return (received, sent);
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
